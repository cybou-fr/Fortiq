namespace Fortiq.Domain;

public sealed record RepositoryDescriptor(RepositoryId Id, string Location);

/// <summary>
/// A snapshot as the repository itself describes it. <paramref name="SourceStableId"/> comes from the
/// metadata Fortiq wrote into the repository and is null for a snapshot that carries none, which is
/// deliberately distinguishable from the filesystem path the engine recorded.
/// </summary>
public sealed record SnapshotDescriptor(
    string Id,
    DateTimeOffset CreatedAt,
    string? SourceStableId,
    string SourcePath,
    bool? PointInTime = null);

public sealed record BackupReceipt(
    Guid OperationId,
    RepositoryId RepositoryId,
    string SnapshotId,
    ulong FilesProcessed = 0,
    ulong BytesProcessed = 0);

public sealed record CheckReceipt(Guid OperationId, RepositoryId RepositoryId, bool IsHealthy);

public sealed record RestoreReceipt(
    Guid OperationId,
    RepositoryId RepositoryId,
    string SnapshotId,
    string TargetPath,
    ulong FilesRestored = 0,
    ulong BytesRestored = 0);

/// <summary>
/// What a retention run kept and what it let go. The snapshots it removed are references; whether
/// their data was deleted too is <paramref name="Pruned"/>.
/// </summary>
public sealed record RetentionReceipt(
    Guid OperationId,
    RepositoryId RepositoryId,
    IReadOnlyList<string> KeptSnapshotIds,
    IReadOnlyList<string> RemovedSnapshotIds,
    bool Pruned);
