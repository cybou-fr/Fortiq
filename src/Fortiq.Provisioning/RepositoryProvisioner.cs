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
/// <summary>Which Windows key store a repository's device key is created in.</summary>
/// <remarks>
/// The choice decides who can open it later, and it cannot be changed afterwards without
/// re-provisioning. A user key belongs to the account that created it: convenient for an operator's
/// own machine, and unopenable by a Windows service running as any other identity. A machine key can
/// be opened by identities with rights to the machine store, which is what unattended work needs -
/// and creating one requires administrator rights.
/// </remarks>
public enum DeviceKeyScope
{
    /// <summary>The creating account's key store. Unattended service work cannot use it.</summary>
    CurrentUser,

    /// <summary>The machine key store. Requires administrator rights to create.</summary>
    Machine
}

public sealed record ProvisionedRepository(
    RepositoryDescriptor Repository,
    RecoveryKit Kit,
    string RecoveryMnemonic,
    bool DeviceUnlockAvailable,
    StorageProtection StorageProtection)
{
    /// <summary>
    /// Keeps the mnemonic out of the generated <c>ToString</c>. A record prints every property by
    /// default, and a result object like this one ends up in log lines, exception messages and
    /// debugger output; the recovery material must not travel with it.
    /// </summary>
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Append("Repository = ").Append(Repository.Id)
            .Append(", Kit = ").Append(Kit.RepositoryId)
            .Append(", RecoveryMnemonic = [redacted]")
            .Append(", DeviceUnlockAvailable = ").Append(DeviceUnlockAvailable)
            .Append(", StorageProtection = ").Append(StorageProtection);
        return true;
    }
}

/// <summary>
/// Creates a repository and the recovery kit that can reopen it. This is the step that makes the
/// recovery promise real: once it returns, the repository can be restored with the kit and the
/// mnemonic alone, on a machine that has never seen this one.
/// </summary>
/// <remarks>
/// Provisioning is transactional around one invariant: no recoverable kit means no surviving
/// initialised repository. The kit is written and then proven to open the repository before the
/// operation is declared successful; anything that fails before that proof rolls the repository
/// back, and a run that is killed outright leaves an intent record that a later run cleans up.
/// </remarks>
public sealed class RepositoryProvisioner
{
    private readonly string _engineRoot;
    private readonly string _helperPath;
    private readonly TimeProvider _clock;
    private readonly IObjectStorageCredentialProvider _storage;
    private readonly IStorageProtectionInspector? _protection;

    public RepositoryProvisioner(
        string engineRoot,
        string? passwordHelperPath = null,
        TimeProvider? clock = null,
        IObjectStorageCredentialProvider? storage = null,
        IStorageProtectionInspector? protection = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(engineRoot);
        _engineRoot = Path.GetFullPath(engineRoot);
        _helperPath = passwordHelperPath ?? Path.Combine(AppContext.BaseDirectory, "Fortiq.PasswordHelper.exe");
        _clock = clock ?? TimeProvider.System;
        _storage = storage ?? new NoObjectStorageCredentials();
        _protection = protection;
    }

    /// <summary>Test seam: runs immediately after the repository exists and before the kit is written.</summary>
    internal Func<CancellationToken, Task>? AfterInitialize { get; set; }

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
        bool addDeviceUnlock = true,
        DeviceKeyScope deviceKeyScope = DeviceKeyScope.CurrentUser,
        bool requireImmutableStorage = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryLocation);
        ArgumentException.ThrowIfNullOrWhiteSpace(kitDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        var repositoryPath = RepositoryLocation.Normalize(repositoryLocation);
        var inObjectStorage = RepositoryLocation.IsObjectStorage(repositoryPath);
        var kitPath = Path.GetFullPath(kitDirectory);

        // Asked before anything is created: storage that will not keep what is written to it is a
        // reason not to start, rather than something to discover once a repository exists.
        var protection = _protection is null
            ? StorageProtection.None
            : await _protection.InspectAsync(repositoryPath, cancellationToken);

        if (requireImmutableStorage && !protection.Immutable)
        {
            throw new StorageNotImmutableException(
                _protection is null
                    ? "Immutable storage was required, but nothing was supplied that can ask the storage what it protects."
                    : "Immutable storage was required, and this storage does not keep what is written to it.");
        }

        if (Directory.Exists(kitPath) && Directory.EnumerateFileSystemEntries(kitPath).Any())
        {
            // Overwriting a kit would destroy the only way back into an existing repository.
            throw new InvalidOperationException("The recovery kit directory must be empty.");
        }

        // Rollback is only ever undoing this run's own work, so an existing repository with content
        // is refused rather than adopted.
        if (!inObjectStorage
            && Directory.Exists(repositoryPath)
            && Directory.EnumerateFileSystemEntries(repositoryPath).Any())
        {
            throw new InvalidOperationException("The repository directory must be empty.");
        }

        using var engine = await VerifyEngineAsync(cancellationToken);
        var intent = await ProvisioningIntent.BeginAsync(workingDirectory, repositoryPath, kitPath, cancellationToken);
        var deviceUnlock = addDeviceUnlock && WindowsTpmEnvelope.IsAvailable;
        KeyEnvelopeV1? deviceEnvelope = null;

        try
        {
            var engineUnlockSecret = RandomNumberGenerator.GetBytes(EnginePasswordV1Encoder.EngineUnlockSecretSize);
            var mnemonic = Bip39Mnemonic.Create();
            using var lease = new BufferKeyLease(engineUnlockSecret);
            CryptographicOperations.ZeroMemory(engineUnlockSecret);

            using var credentials = new PasswordPipeCredentialProvider(_helperPath, lease);
            var adapter = ResticEngineFactory.Create(engine, credentials, workingDirectory, _storage);
            var repository = await adapter.InitializeAsync(new InitializeRepository(repositoryPath), cancellationToken);

            if (AfterInitialize is not null)
            {
                await AfterInitialize(cancellationToken);
            }

            var envelopes = new List<KeyEnvelopeV1>
            {
                Bip39RecoveryEnvelope.Wrap(repository.Id.ToArray(), mnemonic, lease, clock: _clock)
            };

            if (deviceUnlock && OperatingSystem.IsWindows())
            {
                // A machine-scoped key can be opened by the Windows service; a user-scoped one
                // cannot, however correct the kit is. Creating a machine key needs administrator
                // rights, so the caller chooses and the result says which was made rather than
                // quietly producing a key the service will never open.
                deviceEnvelope = WindowsTpmEnvelope.Wrap(
                    repository.Id.ToArray(),
                    lease,
                    DeviceKeyName(repository.Id),
                    machineKey: deviceKeyScope == DeviceKeyScope.Machine,
                    _clock);
                envelopes.Add(deviceEnvelope);
            }

            var kit = await RecoveryKitStore.WriteAsync(
                kitPath,
                repository.Location,
                new RecoveryKitEngine(engine.Name, engine.Version, engine.Sha256),
                envelopes,
                _clock,
                cancellationToken,
                new RecoveryKitStorageProtection(
                    protection.Immutable,
                    protection.Mode.ToString().ToLowerInvariant(),
                    protection.DefaultRetention is { } retention ? (int)Math.Round(retention.TotalDays) : null));

            await ProveTheKitOpensTheRepositoryAsync(engine, kitPath, repository, mnemonic, workingDirectory, cancellationToken);

            await intent.CompleteAsync(cancellationToken);
            return new ProvisionedRepository(repository, kit, mnemonic, deviceUnlock, protection);
        }
        catch
        {
            // Nothing that can be recovered was produced, so nothing that looks like a repository is
            // left behind. A directory that cannot be removed is reported rather than hidden.
            if (deviceEnvelope is not null && OperatingSystem.IsWindows())
            {
                WindowsTpmEnvelope.DeleteKey(deviceEnvelope);
            }

            // A repository in object storage cannot be rolled back from here, and in a locked bucket
            // it cannot be removed at all - by design. Keep a durable intent that names the orphan
            // instead of deleting the only local evidence that manual cleanup is required.
            ProvisioningIntent.RollBack(inObjectStorage ? null : repositoryPath, kitPath);

            if (inObjectStorage)
            {
                try
                {
                    await intent.MarkRemoteCleanupRequiredAsync(CancellationToken.None);
                }
                catch (Exception intentError) when (intentError is IOException or UnauthorizedAccessException)
                {
                    // The original in-progress intent is already durable and still names the remote
                    // repository. Preserve the provisioning failure rather than replacing it with a
                    // secondary failure while updating the more specific state.
                }
            }
            else
            {
                // Local rollback completed, so no operator action remains.
                await intent.CompleteAsync(CancellationToken.None);
            }

            throw;
        }
    }

    /// <summary>
    /// Finishes a provisioning run that was killed before it could finish, by removing the
    /// repository and kit it had started. A run that completed leaves no intent behind, so this is a
    /// no-op after a successful provisioning.
    /// </summary>
    public static async Task<bool> CleanUpInterruptedAsync(string workingDirectory, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        var intent = await ProvisioningIntent.ReadAsync(workingDirectory, cancellationToken);
        if (intent is null)
        {
            return false;
        }

        if (RepositoryLocation.IsObjectStorage(intent.RepositoryPath))
        {
            throw new InvalidOperationException(
                $"The unfinished provisioning run left an object-storage repository at '{intent.RepositoryPath}'. " +
                "Fortiq cannot safely remove remote repository objects automatically; inspect and clean it up manually before removing the intent.");
        }

        ProvisioningIntent.RollBack(intent.RepositoryPath, intent.KitPath);
        ProvisioningIntent.Remove(workingDirectory);
        return true;
    }

    /// <summary>
    /// The device key is named after the repository it unlocks, so one machine can hold a key per
    /// repository and a later run can find the right one.
    /// </summary>
    public static string DeviceKeyName(RepositoryId repositoryId) =>
        $"fortiq/{repositoryId.ToString().ToLowerInvariant()}/engine-unlock";

    /// <summary>
    /// Reads the kit back from disk and uses it to open the repository, so success means the kit was
    /// demonstrated to work rather than assumed to.
    /// </summary>
    private async Task ProveTheKitOpensTheRepositoryAsync(
        VerifiedEngine engine,
        string kitPath,
        RepositoryDescriptor repository,
        string mnemonic,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var opened = await RecoveryKitStore.ReadAsync(kitPath, cancellationToken);
        var recovery = opened.Envelopes.SingleOrDefault(envelope => envelope.Suite == Bip39RecoveryEnvelope.SuiteId)
            ?? throw new InvalidOperationException("The kit that was written holds no recovery method.");

        using var lease = Bip39RecoveryEnvelope.Unwrap(recovery, repository.Id.ToArray(), mnemonic);
        using var credentials = new PasswordPipeCredentialProvider(_helperPath, lease);
        var adapter = ResticEngineFactory.Create(engine, credentials, workingDirectory, _storage);

        // Listing is the cheapest operation that still requires the engine to accept the password
        // the kit produced.
        await adapter.ListSnapshotsAsync(new ListSnapshots(repository), cancellationToken);
    }

    private async Task<VerifiedEngine> VerifyEngineAsync(CancellationToken cancellationToken)
    {
        var manifest = await EngineManifestReader.ReadAsync(Path.Combine(_engineRoot, "manifest.json"), cancellationToken);
        var entry = manifest.Engines.SingleOrDefault(candidate => candidate.Name == "restic" && candidate.Rid == "win-x64")
            ?? throw new InvalidDataException("Pinned restic entry missing.");
        return await EngineBinaryVerifier.VerifyAsync(_engineRoot, entry, cancellationToken);
    }
}
