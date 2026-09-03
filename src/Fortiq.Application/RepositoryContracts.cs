using Fortiq.Domain;

namespace Fortiq.Application;

public sealed record InitializeRepository(string Location);

public sealed record CreateSnapshot(RepositoryDescriptor Repository, string SourcePath, string SourceStableId);

public sealed record ListSnapshots(RepositoryDescriptor Repository);

public sealed record CheckRepository(RepositoryDescriptor Repository);

public sealed record RestoreSnapshot(RepositoryDescriptor Repository, string SnapshotId, string TargetPath);

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
}
