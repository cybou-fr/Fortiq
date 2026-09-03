using Fortiq.Domain;

namespace Fortiq.Application;

public sealed record InitializeRepository(string Location);

public sealed record CreateSnapshot(RepositoryDescriptor Repository, string SourcePath, string SourceStableId);

public sealed record ListSnapshots(RepositoryDescriptor Repository);

public sealed record CheckRepository(RepositoryDescriptor Repository);

/// <summary>
/// Recovers a repository whose previous operation was interrupted, by removing the locks that run
/// left behind. It never removes repository data.
/// </summary>
public sealed record ReconcileRepository(RepositoryDescriptor Repository);

/// <summary>
/// Restores a snapshot into <paramref name="TargetPath"/>. When <paramref name="SourcePath"/> is set,
/// only that backed-up source subtree is restored and it lands directly in the target, so the restore
/// does not have to recreate the intermediate directories of the original absolute path.
/// </summary>
public sealed record RestoreSnapshot(
    RepositoryDescriptor Repository,
    string SnapshotId,
    string TargetPath,
    string? SourcePath = null);

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

    Task<CheckReceipt> CheckAsync(
        CheckRepository command,
        CancellationToken cancellationToken);

    Task<RestoreReceipt> RestoreAsync(
        RestoreSnapshot command,
        CancellationToken cancellationToken);

    Task ReconcileAsync(
        ReconcileRepository command,
        CancellationToken cancellationToken);
}
