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
/// Applies a repository's retention policy with nobody present.
/// </summary>
/// <remarks>
/// The composition is the same one backups and drills use, and that is the point: retention goes
/// through the run registry, where it takes the repository exclusively. A forget that landed while a
/// backup was in flight would apply the policy to a set of snapshots that no longer exists, and one
/// that landed during a restore could remove the snapshot being read - which would surface as a
/// drill that failed to prove recovery, a false alarm about the single thing this product exists to
/// report on.
/// </remarks>
public sealed class UnattendedRetention : IScheduledRetention
{
    private readonly string _engineRoot;
    private readonly string _helperPath;
    private readonly string _workingDirectory;
    private readonly string _runDirectory;
    private readonly string _receiptDirectory;

    /// <summary>Where this operation records its ledger head, outside the receipt directory.</summary>
    private readonly IAuditLedgerAnchor? _auditAnchor;
    private readonly IObjectStorageCredentialProvider _storage;

    public UnattendedRetention(
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

    [SupportedOSPlatform("windows")]
    public async Task<RetentionReceipt> RunAsync(BackupSchedule schedule, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(schedule);

        if (!schedule.RetentionConfigured)
        {
            throw new InvalidOperationException(
                "This schedule has no retention policy, so there is nothing to apply. Silence means keep everything.");
        }

        using var engine = await VerifyEngineAsync(cancellationToken);
        var kit = await RecoveryKitStore.ReadAsync(schedule.KitDirectory, cancellationToken);
        RecoveryKitPolicy.CompareEngine(kit.Manifest, engine.Name, engine.Version, engine.Sha256);

        var device = kit.Envelopes.SingleOrDefault(envelope => envelope.Suite == WindowsTpmEnvelope.SuiteId)
            ?? throw new UnattendedUnlockUnavailableException(
                "This recovery kit has no device-bound unlock method, so retention cannot run without a person.");

        using var lease = WindowsTpmEnvelope.Unwrap(device, device.RepositoryId);
        using var credentials = new PasswordPipeCredentialProvider(_helperPath, lease);

        var working = Path.Combine(_workingDirectory, "engine", schedule.Id);
        Directory.CreateDirectory(working);

        var restic = ResticEngineFactory.Create(engine, credentials, working, _storage);
        var repository = new RepositoryDescriptor(
            RepositoryId.FromBytes(device.RepositoryId),
            schedule.RepositoryLocation);

        RecoveryKitPolicy.RequireSameRepository(
            kit.Manifest,
            (await restic.ReadRepositoryIdAsync(repository, cancellationToken)).ToArray());

        var adapter = new ReceiptRecordingBackupRepository(
            new RegisteredRunBackupRepository(restic, new FileSystemRepositoryRunRegistry(_runDirectory)),
            new EngineIdentity(engine.Name, engine.Version, engine.Sha256),
            new FileSystemOperationReceiptStore(_receiptDirectory, _auditAnchor));

        return await adapter.ApplyRetentionAsync(
            new ApplyRetention(repository, schedule.Retention!, schedule.Prune),
            cancellationToken);
    }

    private async Task<VerifiedEngine> VerifyEngineAsync(CancellationToken cancellationToken)
    {
        var manifest = await EngineManifestReader.ReadAsync(Path.Combine(_engineRoot, "manifest.json"), cancellationToken);
        var entry = manifest.Engines.SingleOrDefault(candidate => candidate.Name == "restic" && candidate.Rid == "win-x64")
            ?? throw new InvalidDataException("Pinned restic entry missing.");
        return await EngineBinaryVerifier.VerifyAsync(_engineRoot, entry, cancellationToken);
    }
}
