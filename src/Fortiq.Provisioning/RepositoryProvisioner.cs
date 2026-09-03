using System.Security.Cryptography;
using Fortiq.Application;
using Fortiq.Domain;
using Fortiq.Infrastructure.Keys;
using Fortiq.Infrastructure.Restic;

namespace Fortiq.Provisioning;

/// <summary>
/// The result of creating a protected repository. The mnemonic is returned exactly once, because it
/// is the only copy: it is not written into the kit, the repository or any log, and Fortiq cannot
/// produce it again.
/// </summary>
public sealed record ProvisionedRepository(
    RepositoryDescriptor Repository,
    RecoveryKit Kit,
    string RecoveryMnemonic,
    bool DeviceUnlockAvailable);

/// <summary>
/// Creates a repository and the recovery kit that can reopen it. This is the step that makes the
/// recovery promise real: once it returns, the repository can be restored with the kit and the
/// mnemonic alone, on a machine that has never seen this one.
/// </summary>
public sealed class RepositoryProvisioner
{
    private readonly string _engineRoot;
    private readonly string _helperPath;
    private readonly TimeProvider _clock;

    public RepositoryProvisioner(string engineRoot, string? passwordHelperPath = null, TimeProvider? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(engineRoot);
        _engineRoot = Path.GetFullPath(engineRoot);
        _helperPath = passwordHelperPath ?? Path.Combine(AppContext.BaseDirectory, "Fortiq.PasswordHelper.exe");
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// Creates the repository and its kit. When the machine has a platform crypto provider a
    /// device-bound envelope is added beside the recovery one, so day-to-day work does not need the
    /// mnemonic - but the mnemonic remains the way back, and the kit refuses to rely on the device
    /// alone.
    /// </summary>
    public async Task<ProvisionedRepository> CreateAsync(
        string repositoryLocation,
        string kitDirectory,
        string workingDirectory,
        CancellationToken cancellationToken,
        bool addDeviceUnlock = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryLocation);
        ArgumentException.ThrowIfNullOrWhiteSpace(kitDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        if (Directory.Exists(kitDirectory) && Directory.EnumerateFileSystemEntries(kitDirectory).Any())
        {
            // Overwriting a kit would destroy the only way back into an existing repository.
            throw new InvalidOperationException("The recovery kit directory must be empty.");
        }

        var engineUnlockSecret = RandomNumberGenerator.GetBytes(EnginePasswordV1Encoder.EngineUnlockSecretSize);
        var mnemonic = Bip39Mnemonic.Create();
        using var lease = new BufferKeyLease(engineUnlockSecret);
        CryptographicOperations.ZeroMemory(engineUnlockSecret);

        using var engine = await VerifyEngineAsync(cancellationToken);
        using var credentials = new PasswordPipeCredentialProvider(_helperPath, lease);
        var adapter = ResticEngineFactory.Create(engine, credentials, workingDirectory);

        var repository = await adapter.InitializeAsync(new InitializeRepository(repositoryLocation), cancellationToken);

        // The kit is written only after the repository exists, so a kit never points at nothing.
        var envelopes = new List<KeyEnvelopeV1>
        {
            Bip39RecoveryEnvelope.Wrap(repository.Id.ToArray(), mnemonic, lease, clock: _clock)
        };

        var deviceUnlock = addDeviceUnlock && WindowsTpmEnvelope.IsAvailable;
        if (deviceUnlock && OperatingSystem.IsWindows())
        {
            envelopes.Add(WindowsTpmEnvelope.Wrap(
                repository.Id.ToArray(),
                lease,
                DeviceKeyName(repository.Id),
                machineKey: false,
                _clock));
        }

        var kit = await RecoveryKitStore.WriteAsync(
            kitDirectory,
            repository.Location,
            new RecoveryKitEngine(engine.Name, engine.Version, engine.Sha256),
            envelopes,
            _clock,
            cancellationToken);

        return new ProvisionedRepository(repository, kit, mnemonic, deviceUnlock);
    }

    /// <summary>
    /// The device key is named after the repository it unlocks, so one machine can hold a key per
    /// repository and a later run can find the right one.
    /// </summary>
    public static string DeviceKeyName(RepositoryId repositoryId) =>
        $"fortiq/{repositoryId.ToString().ToLowerInvariant()}/engine-unlock";

    private async Task<VerifiedEngine> VerifyEngineAsync(CancellationToken cancellationToken)
    {
        var manifest = await EngineManifestReader.ReadAsync(Path.Combine(_engineRoot, "manifest.json"), cancellationToken);
        var entry = manifest.Engines.SingleOrDefault(candidate => candidate.Name == "restic" && candidate.Rid == "win-x64")
            ?? throw new InvalidDataException("Pinned restic entry missing.");
        return await EngineBinaryVerifier.VerifyAsync(_engineRoot, entry, cancellationToken);
    }
}
