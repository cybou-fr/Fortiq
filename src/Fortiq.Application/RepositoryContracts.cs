using Fortiq.Domain;

namespace Fortiq.Application;

/// <summary>
/// Every command carries the operation ID that identifies one Fortiq operation end to end: the
/// engine invocation, the password handover, the receipt and the returned result all use it. A
/// caller that leaves it empty gets one assigned, and the assigned value is what the result and the
/// receipt report.
/// </summary>
public interface IOperationCommand
{
    Guid OperationId { get; }
}

public sealed record InitializeRepository(string Location, Guid OperationId = default) : IOperationCommand;

/// <summary>How the source is read while it is being backed up.</summary>
public enum SourceConsistency
{
    /// <summary>
    /// Read from the live filesystem. A file that changes while it is read is backed up as whatever
    /// was there at the time, which is honest but not a point in time.
    /// </summary>
    Live,

    /// <summary>
    /// Read from a filesystem snapshot, so the whole source is one point in time. Creating one needs
    /// backup privileges; without them the backup fails rather than quietly falling back to live.
    /// </summary>
    FileSystemSnapshot
}

public sealed record CreateSnapshot(
    RepositoryDescriptor Repository,
    string SourcePath,
    string SourceStableId,
    SourceConsistency Consistency = SourceConsistency.Live,
    Guid OperationId = default) : IOperationCommand;

public sealed record ListSnapshots(RepositoryDescriptor Repository, Guid OperationId = default) : IOperationCommand;

public sealed record CheckRepository(RepositoryDescriptor Repository, Guid OperationId = default) : IOperationCommand;

/// <summary>
/// Recovers a repository whose previous operation was interrupted, by removing the locks that run
/// left behind. It never removes repository data.
/// </summary>
public sealed record ReconcileRepository(RepositoryDescriptor Repository, Guid OperationId = default) : IOperationCommand;

/// <summary>
/// Restores a snapshot into <paramref name="TargetPath"/>. When <paramref name="SourcePath"/> is set,
/// only that backed-up source subtree is restored and it lands directly in the target, so the restore
/// does not have to recreate the intermediate directories of the original absolute path.
/// </summary>
public sealed record RestoreSnapshot(
    RepositoryDescriptor Repository,
    string SnapshotId,
    string TargetPath,
    string? SourcePath = null,
    Guid OperationId = default) : IOperationCommand;

public sealed record SnapshotFileEntry(
    string Name,
    string Path,
    string Type,
    ulong Size,
    DateTimeOffset? ModifiedAt);

public sealed record ListSnapshotFiles(
    RepositoryDescriptor Repository,
    string SnapshotId,
    Guid OperationId = default) : IOperationCommand;

/// <summary>
/// Reads the identity the repository states about itself. A path is not an identity: two paths can
/// hold different repositories, and the same repository can be reached by several paths.
/// </summary>
public interface IRepositoryIdentityReader
{
    Task<RepositoryId> ReadRepositoryIdAsync(RepositoryDescriptor repository, CancellationToken cancellationToken);
}

public interface IBackupRepository
{
    Task<RepositoryDescriptor> InitializeAsync(
        InitializeRepository command,
        CancellationToken cancellationToken);

    Task<BackupReceipt> CreateSnapshotAsync(
        CreateSnapshot command,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<SnapshotDescriptor>> ListSnapshotsAsync(
        ListSnapshots query,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<SnapshotFileEntry>> ListFilesAsync(
        ListSnapshotFiles query,
        CancellationToken cancellationToken);

    Task<CheckReceipt> CheckAsync(
        CheckRepository command,
        CancellationToken cancellationToken);

    Task<RestoreReceipt> RestoreAsync(
        RestoreSnapshot command,
        CancellationToken cancellationToken);

    Task ReconcileAsync(
        ReconcileRepository command,
        CancellationToken cancellationToken);

    /// <summary>
    /// Applies a retention policy. It never removes the last snapshot of a source: the plan is read
    /// before anything is done, and a plan that would empty a source is refused.
    /// </summary>
    Task<RetentionReceipt> ApplyRetentionAsync(
        ApplyRetention command,
        CancellationToken cancellationToken);
}

/// <summary>Everything a repository engine offers, as one composition root returns it.</summary>
public interface IRepositoryEngine : IBackupRepository, IRepositoryIdentityReader;
