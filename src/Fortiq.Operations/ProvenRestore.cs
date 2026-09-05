using System.Runtime.Versioning;
using Fortiq.Application;
using Fortiq.Domain;
using Fortiq.Infrastructure.Keys;
using Fortiq.Infrastructure.Receipts;
using Fortiq.Infrastructure.Restic;
using Fortiq.Infrastructure.Runs;
using Fortiq.Scheduling;

namespace Fortiq.Operations;

/// <summary>Raised when a drill will not start because the machine cannot afford it.</summary>
public sealed class DrillWorkspaceUnavailableException : Exception
{
    public DrillWorkspaceUnavailableException(string message)
        : base(message)
    {
    }

    public DrillWorkspaceUnavailableException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    public DrillWorkspaceUnavailableException()
        : base("There is not enough free space to prove a restore.")
    {
    }
}

/// <summary>Raised when a restore ran but did not put back what the snapshot says it holds.</summary>
public sealed class RestoreProofFailedException : Exception
{
    public RestoreProofFailedException(string message)
        : base(message)
    {
    }

    public RestoreProofFailedException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    public RestoreProofFailedException()
        : base("The restore did not produce what the snapshot says it should.")
    {
    }
}

/// <summary>
/// What a proof attempt found. <paramref name="NodesRestored"/> is what the engine says it put back
/// and counts directories as well as files; <paramref name="FilesOnDisk"/> is what was then counted
/// in the restored tree. The two are not the same number and are not meant to be.
/// </summary>
public sealed record RestoreProof(
    string RepositoryId,
    string SnapshotId,
    DateTimeOffset SnapshotCreatedAt,
    ulong NodesRestored,
    ulong FilesOnDisk,
    ulong BytesRestored);

/// <summary>
/// Proves that a repository can be recovered, the only way that can be proven: by restoring from it
/// and looking at what came out.
/// </summary>
/// <remarks>
/// The proof is deliberately not "the restore command exited zero". A restore is asked for the most
/// recent snapshot, and what landed on disk is then counted and measured against what the engine
/// said it wrote. A restore that reports success over an empty directory is exactly the failure this
/// is meant to catch, and it is the one an exit code cannot see.
/// <para>
/// Unlocking uses the device envelope, as unattended work does. The mnemonic is the way back from a
/// machine that has lost everything; asking for it to run a routine check here would train people to
/// type it, which is the habit that loses it.
/// </para>
/// </remarks>
public sealed class ProvenRestore
{
    private readonly string _engineRoot;
    private readonly string _workingDirectory;
    private readonly string _helperPath;
    private readonly string _runDirectory;
    private readonly string _receiptDirectory;
    private readonly IObjectStorageCredentialProvider _storage;
    private readonly string? _workspaceRoot;
    private readonly long _freeSpaceFloor;

    public ProvenRestore(
        string engineRoot,
        string workingDirectory,
        string? passwordHelperPath = null,
        string? runDirectory = null,
        string? receiptDirectory = null,
        IObjectStorageCredentialProvider? storage = null,
        string? workspaceRoot = null,
        long freeSpaceFloor = DefaultFreeSpaceFloor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(engineRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        _engineRoot = Path.GetFullPath(engineRoot);
        _workingDirectory = Path.GetFullPath(workingDirectory);
        _helperPath = passwordHelperPath ?? Path.Combine(AppContext.BaseDirectory, "Fortiq.PasswordHelper.exe");
        _runDirectory = runDirectory ?? FortiqRunDirectory.Default();
        // Derived from the working directory the caller supplied, which is how it stays consistent
        // with FortiqStatePaths when that is where the working directory came from. Callers that
        // share evidence across processes pass the directory explicitly rather than relying on this.
        _receiptDirectory = receiptDirectory ?? Path.Combine(_workingDirectory, "receipts");
        _storage = storage ?? new NoObjectStorageCredentials();
        _workspaceRoot = workspaceRoot is null ? null : Path.GetFullPath(workspaceRoot);
        _freeSpaceFloor = freeSpaceFloor;
    }

    /// <summary>
    /// How much room must remain on the volume before a drill will start. A drill restores a whole
    /// source, and a source can be far larger than the system disk that the temporary directory
    /// usually lives on; an unattended weekly drill that filled that disk would break the machine it
    /// was meant to reassure.
    /// </summary>
    public const long DefaultFreeSpaceFloor = 20L * 1024 * 1024 * 1024;

    [SupportedOSPlatform("windows")]
    public async Task<RestoreProof> ProveAsync(BackupSchedule schedule, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(schedule);

        var kit = await RecoveryKitStore.ReadAsync(schedule.KitDirectory, cancellationToken);

        return await new RestoreProofRecorder(new FileSystemOperationReceiptStore(_receiptDirectory)).RecordAsync(
            kit.Manifest.RepositoryId,
            new EngineIdentity(kit.Manifest.Engine.Name, kit.Manifest.Engine.Version, kit.Manifest.Engine.Sha256),
            async () =>
            {
                using var engine = await VerifyEngineAsync(cancellationToken);
                RecoveryKitPolicy.CompareEngine(kit.Manifest, engine.Name, engine.Version, engine.Sha256);
                return await ProveCoreAsync(schedule, engine, kit, cancellationToken);
            });
    }

    [SupportedOSPlatform("windows")]
    private async Task<RestoreProof> ProveCoreAsync(
        BackupSchedule schedule, VerifiedEngine engine, OpenedRecoveryKit kit, CancellationToken cancellationToken)
    {

        var device = kit.Envelopes.SingleOrDefault(envelope => envelope.Suite == WindowsTpmEnvelope.SuiteId)
            ?? throw new UnattendedUnlockUnavailableException(
                "This recovery kit has no device-bound unlock method, so a restore cannot be proven without the recovery words.");

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

        var registry = new FileSystemRepositoryRunRegistry(_runDirectory);
        var adapter = new ReceiptRecordingBackupRepository(
            new RegisteredRunBackupRepository(restic, registry),
            new EngineIdentity(engine.Name, engine.Version, engine.Sha256),
            new FileSystemOperationReceiptStore(_receiptDirectory));

        // Choosing a snapshot and restoring it are two operations, and each registers a run of its
        // own. Between them the repository is unheld, so a retention run - which takes the
        // repository to itself - could forget the snapshot that was just chosen. The restore would
        // then fail, and be recorded as a drill that could not prove recovery: a false alarm about
        // the one thing this product exists to report on. One shared run held across both closes
        // that window; shared runs nest, so the operations underneath still register normally.
        await using var drill = await registry.BeginAsync(
            repository.Id,
            OperationKind.Restore,
            Guid.NewGuid(),
            RunExclusivity.Shared,
            cancellationToken);

        var snapshots = await adapter.ListSnapshotsAsync(new ListSnapshots(repository), cancellationToken);
        var latest = snapshots.OrderByDescending(snapshot => snapshot.CreatedAt).FirstOrDefault()
            ?? throw new RestoreProofFailedException(
                "This repository holds no snapshots, so there is nothing to prove a recovery from.");

        // Restored somewhere disposable, not over the source. A proof that overwrote live files would
        // make the check more dangerous than the failure it is looking for.
        var target = CreateWorkspace();
        try
        {
            var receipt = await adapter.RestoreAsync(
                // The snapshot's own source subtree, not the whole snapshot. A full-tree restore
                // recreates the drive-letter path the files came from, which a scratch directory is
                // the wrong place for; this puts back exactly the folder that was backed up.
                new RestoreSnapshot(repository, latest.Id, target.FullName, latest.SourcePath),
                cancellationToken);

            var written = Measure(target.FullName);
            if (written.Files == 0 || written.Bytes == 0)
            {
                throw new RestoreProofFailedException(
                    "The restore reported success, but the restored tree holds nothing.");
            }

            // Bytes are comparable and are compared exactly. The engine's node count is not: it
            // counts directories alongside files, so the files on disk can only be fewer, and an
            // equality check there would fail on every snapshot that has a subfolder in it.
            if (written.Bytes != receipt.BytesRestored)
            {
                throw new RestoreProofFailedException(
                    $"The restore claimed {receipt.BytesRestored} bytes, but {written.Bytes} bytes are on disk.");
            }

            if (written.Files > receipt.FilesRestored)
            {
                throw new RestoreProofFailedException(
                    $"The restored tree holds {written.Files} files, more than the {receipt.FilesRestored} "
                    + "entries the restore claims to have written.");
            }

            return new RestoreProof(
                repository.Id.ToString(),
                latest.Id,
                latest.CreatedAt,
                receipt.FilesRestored,
                written.Files,
                receipt.BytesRestored);
        }
        finally
        {
            Discard(target);
        }
    }

    /// <summary>
    /// Removes the scratch copy. Restored files keep the attributes they were backed up with, so a
    /// read-only file comes back read-only and refuses to be deleted until that is cleared.
    /// </summary>
    /// <remarks>
    /// A failure to clean up cannot unmake a restore that already succeeded, so it is not allowed to
    /// replace the answer: the residue sits in the temporary directory, which is where the operating
    /// system expects to find abandoned scratch data.
    /// </remarks>
    private static void Discard(DirectoryInfo target)
    {
        try
        {
            foreach (var path in Directory.EnumerateFiles(target.FullName, "*", SearchOption.AllDirectories))
            {
                var file = new FileInfo(path);
                if (file.IsReadOnly)
                {
                    file.IsReadOnly = false;
                }
            }

            target.Delete(recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Opens the scratch directory, refusing before anything is restored if the volume is already
    /// low on room.
    /// </summary>
    /// <remarks>
    /// This is a floor, not a size-aware policy: it does not yet ask how large the snapshot is, so a
    /// source bigger than the remaining space can still fill the volume once the drill is under way.
    /// What it does prevent is the case that needs no arithmetic - starting a full restore onto a
    /// disk that was already nearly full. Refusing is reported as a failed drill, which is honest:
    /// recovery was not proven, and the reason names the volume and the space it had.
    /// </remarks>
    private DirectoryInfo CreateWorkspace()
    {
        if (_workspaceRoot is null)
        {
            var temporary = Directory.CreateTempSubdirectory("fortiq-proof-");
            RequireRoom(temporary.FullName);
            return temporary;
        }

        Directory.CreateDirectory(_workspaceRoot);
        RequireRoom(_workspaceRoot);

        // Named from a fresh GUID rather than a counter: two drills may overlap, and a drill must
        // never restore into a directory another one is already using.
        return Directory.CreateDirectory(Path.Combine(_workspaceRoot, "fortiq-proof-" + Guid.NewGuid().ToString("N")));
    }

    private void RequireRoom(string path)
    {
        if (_freeSpaceFloor <= 0)
        {
            return;
        }

        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!);
            if (drive.AvailableFreeSpace < _freeSpaceFloor)
            {
                throw new DrillWorkspaceUnavailableException(
                    $"A restore drill needs room to put the data back. {drive.Name} has "
                    + $"{drive.AvailableFreeSpace / (1024 * 1024)} MB free, below the "
                    + $"{_freeSpaceFloor / (1024 * 1024)} MB this drill requires before it will start.");
            }
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException)
        {
            // A volume that cannot be queried is not a reason to refuse: on a network or mapped
            // location the drill may still be perfectly affordable, and refusing every drill because
            // free space is unknowable would quietly stop proving recovery at all.
        }
    }

    private static (ulong Files, ulong Bytes) Measure(string directory)
    {
        ulong files = 0;
        ulong bytes = 0;
        foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            files++;
            bytes += (ulong)new FileInfo(path).Length;
        }

        return (files, bytes);
    }

    private async Task<VerifiedEngine> VerifyEngineAsync(CancellationToken cancellationToken)
    {
        var manifest = await EngineManifestReader.ReadAsync(Path.Combine(_engineRoot, "manifest.json"), cancellationToken);
        var entry = manifest.Engines.SingleOrDefault(candidate => candidate.Name == "restic" && candidate.Rid == "win-x64")
            ?? throw new InvalidDataException("Pinned restic entry missing.");
        return await EngineBinaryVerifier.VerifyAsync(_engineRoot, entry, cancellationToken);
    }
}
