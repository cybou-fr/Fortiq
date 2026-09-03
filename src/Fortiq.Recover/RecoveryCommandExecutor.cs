using System.Security.Cryptography;
using Fortiq.Application;
using Fortiq.Domain;
using Fortiq.Infrastructure.Keys;
using Fortiq.Infrastructure.Restic;

namespace Fortiq.Recover;

/// <summary>
/// Runs a recovery command against a repository, a pinned engine and a recovery kit. It depends on
/// nothing but those three: no Fortiq service, no local state, no network.
/// </summary>
public sealed class RecoveryCommandExecutor : IRecoveryCommandExecutor
{
    private readonly string _helperPath;

    public RecoveryCommandExecutor(string? passwordHelperPath = null) =>
        _helperPath = passwordHelperPath ?? Path.Combine(AppContext.BaseDirectory, "Fortiq.PasswordHelper.exe");

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
            return Inspect(command, engine);
        }

        var envelope = await ReadEnvelopeAsync(command.Envelope!, token);
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
            var adapter = ResticEngineFactory.Create(
                engine,
                new PasswordPipeCredentialProvider(_helperPath, lease),
                workspace.FullName);

            return command.Operation switch
            {
                RecoveryOperation.Snapshots => await SnapshotsAsync(adapter, repository, token),
                RecoveryOperation.Check => await CheckAsync(adapter, repository, token),
                RecoveryOperation.Restore => await RestoreAsync(adapter, repository, command, token),
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

    private static async Task<KeyEnvelopeV1> ReadEnvelopeAsync(string path, CancellationToken token)
    {
        var encoded = await File.ReadAllBytesAsync(path, token);
        return KeyEnvelopeCodec.Decode(encoded);
    }

    private static object Inspect(RecoveryCommand command, VerifiedEngine engine)
    {
        object? envelope = null;
        if (command.Envelope is not null && File.Exists(command.Envelope))
        {
            // Inspect reads only the public header of the kit; it never asks for recovery material.
            var decoded = KeyEnvelopeCodec.Decode(File.ReadAllBytes(command.Envelope));
            envelope = new
            {
                envelopeId = Convert.ToHexStringLower(decoded.EnvelopeId),
                repositoryId = Convert.ToHexStringLower(decoded.RepositoryId),
                providerType = decoded.ProviderType.ToString().ToLowerInvariant(),
                suite = decoded.Suite,
                createdAt = decoded.CreatedAt,
                supported = decoded.Suite == Bip39RecoveryEnvelope.SuiteId
            };
        }

        return new
        {
            schema = "fortiq.recovery-inspect",
            version = 1,
            repository = command.Repository,
            repositoryPresent = File.Exists(Path.Combine(command.Repository, "config")),
            engine = new { engine.Name, engine.Version, engine.Rid, engine.Sha256 },
            envelope,
            unlockRequired = true
        };
    }

    private static async Task<object> SnapshotsAsync(IBackupRepository adapter, RepositoryDescriptor repository, CancellationToken token)
    {
        var snapshots = await adapter.ListSnapshotsAsync(new ListSnapshots(repository), token);
        return new
        {
            schema = "fortiq.recovery-snapshots",
            version = 1,
            repositoryId = repository.Id.ToString(),
            snapshots = snapshots
                .Select(snapshot => new
                {
                    id = snapshot.Id,
                    createdAt = snapshot.CreatedAt,
                    // Read from the repository's own metadata; null means the snapshot carries none.
                    source = snapshot.SourceStableId,
                    path = snapshot.SourcePath
                })
                .ToArray()
        };
    }

    private static async Task<object> CheckAsync(IBackupRepository adapter, RepositoryDescriptor repository, CancellationToken token)
    {
        var receipt = await adapter.CheckAsync(new CheckRepository(repository), token);
        return new
        {
            schema = "fortiq.recovery-check",
            version = 1,
            operationId = receipt.OperationId,
            repositoryId = repository.Id.ToString(),
            healthy = receipt.IsHealthy
        };
    }

    private static async Task<object> RestoreAsync(
        IBackupRepository adapter,
        RepositoryDescriptor repository,
        RecoveryCommand command,
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
        await _prompt.WriteLineAsync("Enter the recovery mnemonic and press Enter:");
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
