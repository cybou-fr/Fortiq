using System.Runtime.Versioning;
using Fortiq.Application;
using Fortiq.Domain;
using Fortiq.Infrastructure.Keys;
using Fortiq.Infrastructure.Receipts;
using Fortiq.Infrastructure.Restic;
using Fortiq.Infrastructure.Runs;
using Fortiq.Scheduling;

namespace Fortiq.Service;

/// <summary>Raised when a schedule cannot run without someone present to supply recovery material.</summary>
public sealed class UnattendedUnlockUnavailableException : Exception
{
    public UnattendedUnlockUnavailableException(string message)
        : base(message)
    {
    }

    public UnattendedUnlockUnavailableException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    public UnattendedUnlockUnavailableException()
        : base("This recovery kit has no unlock method that works without a person.")
    {
    }
}

/// <summary>
/// Runs a scheduled backup with nobody present. Everything a scheduled run needs comes from the
/// machine itself: the kit says which repository, and the device-bound envelope in it opens that
/// repository without any human secret.
/// </summary>
/// <remarks>
/// A recovery mnemonic is deliberately not usable here. It is the way back into a repository from a
/// machine that has lost everything, and a service that could unlock with it unattended would have
/// to hold it - which would make the mnemonic a secret on the machine rather than a secret about it.
/// A kit without a device method therefore fails, plainly, instead of prompting for something no one
/// is there to type.
/// </remarks>
public sealed class UnattendedBackup : IScheduledBackup
{
    private readonly string _engineRoot;
    private readonly string _helperPath;
    private readonly string _workingDirectory;
    private readonly string _runDirectory;
    private readonly string _receiptDirectory;

    public UnattendedBackup(
        string engineRoot,
        string workingDirectory,
        string? passwordHelperPath = null,
        string? runDirectory = null,
        string? receiptDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(engineRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        _engineRoot = Path.GetFullPath(engineRoot);
        _workingDirectory = Path.GetFullPath(workingDirectory);
        _helperPath = passwordHelperPath ?? Path.Combine(AppContext.BaseDirectory, "Fortiq.PasswordHelper.exe");
        _runDirectory = runDirectory ?? FortiqRunDirectory.Default();
        _receiptDirectory = receiptDirectory ?? Path.Combine(_workingDirectory, "receipts");
    }

    [SupportedOSPlatform("windows")]
    public async Task<BackupReceipt> RunAsync(BackupSchedule schedule, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(schedule);

        using var engine = await VerifyEngineAsync(cancellationToken);
        var kit = await RecoveryKitStore.ReadAsync(schedule.KitDirectory, cancellationToken);

        // The kit has to be the kit for this engine before it is used to open anything.
        RecoveryKitPolicy.CompareEngine(kit.Manifest, engine.Name, engine.Version, engine.Sha256);

        var device = kit.Envelopes.SingleOrDefault(envelope => envelope.Suite == WindowsTpmEnvelope.SuiteId)
            ?? throw new UnattendedUnlockUnavailableException(
                "This recovery kit has no device-bound unlock method, so it cannot be opened without a person.");

        var repositoryId = RepositoryId.FromBytes(device.RepositoryId);
        using var lease = WindowsTpmEnvelope.Unwrap(device, device.RepositoryId);
        using var credentials = new PasswordPipeCredentialProvider(_helperPath, lease);

        var working = Path.Combine(_workingDirectory, "engine", schedule.Id);
        Directory.CreateDirectory(working);

        var restic = ResticEngineFactory.Create(engine, credentials, working);
        var repository = new RepositoryDescriptor(repositoryId, schedule.RepositoryLocation);

        // The repository states its own identity, and it has to be the one the kit describes.
        RecoveryKitPolicy.RequireSameRepository(
            kit.Manifest,
            (await restic.ReadRepositoryIdAsync(repository, cancellationToken)).ToArray());

        // The same composition an operator's run uses: the work is registered so a reconciliation
        // cannot clear locks under it, and evidence is written whatever the outcome.
        var adapter = new ReceiptRecordingBackupRepository(
            new RegisteredRunBackupRepository(restic, new FileSystemRepositoryRunRegistry(_runDirectory)),
            new EngineIdentity(engine.Name, engine.Version, engine.Sha256),
            new FileSystemOperationReceiptStore(_receiptDirectory));

        return await adapter.CreateSnapshotAsync(
            new CreateSnapshot(repository, schedule.SourcePath, schedule.SourceStableId, schedule.Consistency),
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
