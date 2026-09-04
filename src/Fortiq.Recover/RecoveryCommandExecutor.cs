using System.Security.Cryptography;
using Fortiq.Application;
using Fortiq.Domain;
using Fortiq.Infrastructure.Keys;
using Fortiq.Infrastructure.Restic;
using Fortiq.Infrastructure.Runs;

namespace Fortiq.Recover;

/// <summary>
/// Runs a recovery command against a repository, a pinned engine and a recovery kit. It depends on
/// nothing but those three: no Fortiq service, no local state, no network.
/// </summary>
public sealed class RecoveryCommandExecutor : IRecoveryCommandExecutor
{
    private readonly string _helperPath;
    private readonly string _runDirectory;
    private readonly IObjectStorageCredentialProvider _storage;

    public RecoveryCommandExecutor(
        string? passwordHelperPath = null,
        string? runDirectory = null,
        IObjectStorageCredentialProvider? storage = null)
    {
        _helperPath = passwordHelperPath ?? Path.Combine(AppContext.BaseDirectory, "Fortiq.PasswordHelper.exe");
        _runDirectory = runDirectory ?? FortiqRunDirectory.Default();
        _storage = storage ?? new NoObjectStorageCredentials();
    }

    public async Task<object> ExecuteAsync(
        RecoveryCommand command,
        IRecoveryMaterialReader material,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(material);

        using var engine = await VerifyEngineAsync(command.EngineRoot, token);
        if (!command.RequiresUnlock)
        {
            return await InspectAsync(command, engine, token);
        }

        var kit = await RecoveryKitStore.ReadAsync(command.Kit!, token);

        // The kit has to describe this engine before it is used to open anything.
        var engineAgreement = RecoveryKitPolicy.CompareEngine(kit.Manifest, engine.Name, engine.Version, engine.Sha256);

        var envelope = kit.Envelopes.SingleOrDefault(candidate => candidate.Suite == Bip39RecoveryEnvelope.SuiteId)
            ?? throw new InvalidDataException("The recovery kit holds no unlock method this tool supports.");
        var mnemonic = await material.ReadMnemonicAsync(token);

        // The unwrapped secret exists only for the duration of this command, and only inside a lease
        // that zeroes its buffer.
        using var lease = Bip39RecoveryEnvelope.Unwrap(envelope, envelope.RepositoryId, mnemonic);
        var repository = new RepositoryDescriptor(
            RepositoryId.FromBytes(envelope.RepositoryId),
            command.Repository);

        var workspace = Directory.CreateTempSubdirectory("fortiq-recover-");
        try
        {
            var restic = ResticEngineFactory.Create(
                engine,
                new PasswordPipeCredentialProvider(_helperPath, lease),
                workspace.FullName,
                _storage);

            // Recovery runs in its own process, so it registers its work like any other run: a
            // reconciliation elsewhere must not clear locks while a restore is in flight.
            var adapter = new RegisteredRunBackupRepository(
                restic,
                new FileSystemRepositoryRunRegistry(_runDirectory));

            // The repository states its own identity once it is open; the kit has to be the kit for
            // that repository, not merely for whatever sits at that path.
            var actual = await restic.ReadRepositoryIdAsync(repository, token);
            RecoveryKitPolicy.RequireSameRepository(kit.Manifest, actual.ToArray());

            return command.Operation switch
            {
                RecoveryOperation.Snapshots => await SnapshotsAsync(adapter, repository, engineAgreement, token),
                RecoveryOperation.Check => await CheckAsync(adapter, repository, engineAgreement, token),
                RecoveryOperation.Restore => await RestoreAsync(adapter, repository, command, engineAgreement, token),
                _ => throw new InvalidDataException("Unsupported recovery operation.")
            };
        }
        finally
        {
            workspace.Delete(recursive: true);
        }
    }

    private static async Task<VerifiedEngine> VerifyEngineAsync(string engineRoot, CancellationToken token)
    {
        var manifest = await EngineManifestReader.ReadAsync(Path.Combine(engineRoot, "manifest.json"), token);
        var entry = manifest.Engines.SingleOrDefault(candidate => candidate.Name == "restic" && candidate.Rid == "win-x64")
            ?? throw new InvalidDataException("Pinned restic entry missing.");
        return await EngineBinaryVerifier.VerifyAsync(engineRoot, entry, token);
    }

    private static async Task<object> InspectAsync(RecoveryCommand command, VerifiedEngine engine, CancellationToken token)
    {
        object? kit = null;
        if (command.Kit is not null)
        {
            // Inspect reads and verifies the public part of the kit - manifest, envelope hashes and
            // the repository each envelope belongs to. It never asks for recovery material.
            var opened = await RecoveryKitStore.ReadAsync(command.Kit, token);
            kit = new
            {
                repositoryId = opened.Manifest.RepositoryId,
                repositoryLocator = opened.Manifest.RepositoryLocator,
                createdAt = opened.Manifest.CreatedAt,
                engine = opened.Manifest.Engine,
                unlockMethods = opened.Manifest.UnlockMethods
                    .Select(method => new
                    {
                        method.ProviderType,
                        method.Suite,
                        method.EnvelopeId,
                        supported = method.Suite == Bip39RecoveryEnvelope.SuiteId
                    })
                    .ToArray(),
                instructions = opened.Manifest.Instructions
            };
        }

        return new
        {
            schema = "fortiq.recovery-inspect",
            version = 1,
            repository = command.Repository,
            repositoryPresent = File.Exists(Path.Combine(command.Repository, "config")),
            engine = new { engine.Name, engine.Version, engine.Rid, engine.Sha256 },
            kit,
            unlockRequired = true
        };
    }

    private static async Task<object> SnapshotsAsync(
        RegisteredRunBackupRepository adapter,
        RepositoryDescriptor repository,
        EngineAgreement engineAgreement,
        CancellationToken token)
    {
        var snapshots = await adapter.ListSnapshotsAsync(new ListSnapshots(repository), token);
        return new
        {
            schema = "fortiq.recovery-snapshots",
            version = 1,
            repositoryId = repository.Id.ToString(),
            engineAgreement = engineAgreement.ToString(),
            snapshots = snapshots
                .Select(snapshot => new
                {
                    id = snapshot.Id,
                    createdAt = snapshot.CreatedAt,
                    // Read from the repository's own metadata; null means the snapshot carries none.
                    source = snapshot.SourceStableId,
                    path = snapshot.SourcePath,
                    // Null where the snapshot records nothing about it, which is not the same as
                    // recording that it was live.
                    pointInTime = snapshot.PointInTime
                })
                .ToArray()
        };
    }

    private static async Task<object> CheckAsync(
        RegisteredRunBackupRepository adapter,
        RepositoryDescriptor repository,
        EngineAgreement engineAgreement,
        CancellationToken token)
    {
        var receipt = await adapter.CheckAsync(new CheckRepository(repository), token);
        return new
        {
            schema = "fortiq.recovery-check",
            version = 1,
            operationId = receipt.OperationId,
            repositoryId = repository.Id.ToString(),
            engineAgreement = engineAgreement.ToString(),
            healthy = receipt.IsHealthy
        };
    }

    private static async Task<object> RestoreAsync(
        RegisteredRunBackupRepository adapter,
        RepositoryDescriptor repository,
        RecoveryCommand command,
        EngineAgreement engineAgreement,
        CancellationToken token)
    {
        var receipt = await adapter.RestoreAsync(
            new RestoreSnapshot(repository, command.SnapshotId!, command.Target!, command.Source),
            token);

        return new
        {
            schema = "fortiq.recovery-restore",
            version = 1,
            operationId = receipt.OperationId,
            repositoryId = repository.Id.ToString(),
            engineAgreement = engineAgreement.ToString(),
            snapshotId = receipt.SnapshotId,
            target = receipt.TargetPath,
            filesRestored = receipt.FilesRestored,
            bytesRestored = receipt.BytesRestored
        };
    }
}

/// <summary>Reads the recovery mnemonic from standard input, never from the command line.</summary>
public sealed class ConsoleRecoveryMaterialReader : IRecoveryMaterialReader
{
    private readonly TextReader _input;
    private readonly TextWriter _prompt;

    public ConsoleRecoveryMaterialReader(TextReader input, TextWriter prompt)
    {
        _input = input;
        _prompt = prompt;
    }

    public async Task<string> ReadMnemonicAsync(CancellationToken token)
    {
        // Only a person needs the prompt. When input is piped, the caller is a program, and the
        // error stream should carry nothing but the failure it may have to act on.
        if (!Console.IsInputRedirected)
        {
            await _prompt.WriteLineAsync("Enter the recovery mnemonic and press Enter:");
        }

        var line = await _input.ReadLineAsync(token);
        if (string.IsNullOrWhiteSpace(line))
        {
            throw new FormatException("No recovery mnemonic was provided on standard input.");
        }

        try
        {
            // Validate here so a typing mistake is reported as such, before any key derivation runs.
            CryptographicOperations.ZeroMemory(Bip39Mnemonic.Decode(line));
            return line;
        }
        finally
        {
            _prompt.Flush();
        }
    }
}
