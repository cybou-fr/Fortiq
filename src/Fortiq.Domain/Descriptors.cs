namespace Fortiq.Domain;

public sealed record RepositoryDescriptor(RepositoryId Id, string Location);

public sealed record SnapshotDescriptor(string Id, DateTimeOffset CreatedAt, string SourceStableId);

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
