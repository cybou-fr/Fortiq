using System.Runtime.Versioning;
using Fortiq.Application;
using Fortiq.Domain;
using Fortiq.Infrastructure.Keys;
using Fortiq.Infrastructure.Receipts;
using Fortiq.Infrastructure.Restic;
using Fortiq.Infrastructure.Runs;
using Fortiq.Scheduling;

namespace Fortiq.Operations;

/// <summary>
/// Clears the lock an interrupted run left behind, so the repository can be written to again.
/// </summary>
/// <remarks>
/// A run that is killed - by a cancelled operation, a power cut, a machine that slept - leaves its
/// lock in the repository, and every later backup fails because the repository is already locked.
/// The engine can clear such a lock, and until now nothing in the product ever asked it to: the
/// capability existed on the engine and had no caller, so the only way out was to run restic by hand
/// against somebody's encrypted repository.
///
/// It is deliberately not automatic. <c>unlock --remove-all</c> clears a lock whose owner cannot be
/// proven dead, which is exactly what makes it able to fix this and exactly what makes it dangerous:
/// a lock held by a second computer backing up to the same repository looks the same from here. The
/// run registry can only speak for this machine. So this is an action somebody takes, having been
/// told what it assumes, rather than a repair Fortiq performs behind them.
///
/// What the registry does guarantee is the other half: the repository is taken exclusively for the
/// duration, so a backup or a drill on this machine cannot be running underneath the clearance.
/// </remarks>
public sealed class StaleLockRecovery
{
    private readonly string _engineRoot;
    private readonly string _helperPath;
    private readonly string _workingDirectory;
    private readonly string _runDirectory;
    private readonly string _receiptDirectory;
    private readonly IAuditLedgerAnchor? _auditAnchor;
    private readonly IObjectStorageCredentialProvider _storage;

    public StaleLockRecovery(
        string engineRoot,
        string workingDirectory,
        string? passwordHelperPath = null,
        string? runDirectory = null,
        string? receiptDirectory = null,
        IObjectStorageCredentialProvider? storage = null,
        IAuditLedgerAnchor? auditAnchor = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(engineRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        _engineRoot = Path.GetFullPath(engineRoot);
        _workingDirectory = Path.GetFullPath(workingDirectory);
        _helperPath = passwordHelperPath ?? Path.Combine(AppContext.BaseDirectory, "Fortiq.PasswordHelper.exe");
        _runDirectory = runDirectory ?? FortiqRunDirectory.Default();
        _receiptDirectory = receiptDirectory ?? Path.Combine(_workingDirectory, "receipts");
        _auditAnchor = auditAnchor;
        _storage = storage ?? new NoObjectStorageCredentials();
    }

    /// <summary>Clears the locks on the repository this schedule backs up to.</summary>
    /// <exception cref="RepositoryBusyException">
    /// Another Fortiq operation on this machine holds the repository. Clearing a lock while a run is
    /// in flight would remove that run's own lock out from under it.
    /// </exception>
    [SupportedOSPlatform("windows")]
    public async Task ClearAsync(BackupSchedule schedule, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(schedule);

        using var engine = await VerifyEngineAsync(cancellationToken);
        var kit = await RecoveryKitStore.ReadAsync(schedule.KitDirectory, cancellationToken);
        RecoveryKitPolicy.CompareEngine(kit.Manifest, engine.Name, engine.Version, engine.Sha256);

        var device = kit.Envelopes.SingleOrDefault(envelope => envelope.Suite == WindowsTpmEnvelope.SuiteId)
            ?? throw new UnattendedUnlockUnavailableException(
                "This recovery kit has no device-bound unlock method, so it cannot be opened without a person.");

        var repositoryId = RepositoryId.FromBytes(device.RepositoryId);
        using var lease = WindowsTpmEnvelope.Unwrap(device, device.RepositoryId);
        using var credentials = new PasswordPipeCredentialProvider(_helperPath, lease);

        var working = Path.Combine(_workingDirectory, "engine", schedule.Id);
        Directory.CreateDirectory(working);

        var restic = ResticEngineFactory.Create(engine, credentials, working, _storage);
        var repository = new RepositoryDescriptor(repositoryId, schedule.RepositoryLocation);

        // The repository states its own identity, and it has to be the one the kit describes - before
        // anything is cleared in it.
        RecoveryKitPolicy.RequireSameRepository(
            kit.Manifest,
            (await restic.ReadRepositoryIdAsync(repository, cancellationToken)).ToArray());

        // The registry takes it exclusively, and the receipt store records that this happened. An
        // operation that removes a safety mechanism is exactly the kind that has to leave evidence.
        var adapter = new ReceiptRecordingBackupRepository(
            new RegisteredRunBackupRepository(restic, new FileSystemRepositoryRunRegistry(_runDirectory)),
            new EngineIdentity(engine.Name, engine.Version, engine.Sha256),
            new FileSystemOperationReceiptStore(_receiptDirectory, _auditAnchor));

        await adapter.ReconcileAsync(new ReconcileRepository(repository), cancellationToken);
    }

    private async Task<VerifiedEngine> VerifyEngineAsync(CancellationToken cancellationToken)
    {
        var manifest = await EngineManifestReader.ReadAsync(Path.Combine(_engineRoot, "manifest.json"), cancellationToken);
        var entry = manifest.Engines.SingleOrDefault(candidate => candidate.Name == "restic" && candidate.Rid == "win-x64")
            ?? throw new InvalidDataException("Pinned restic entry missing.");
        return await EngineBinaryVerifier.VerifyAsync(_engineRoot, entry, cancellationToken);
    }
}
