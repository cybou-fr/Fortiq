namespace Fortiq.Application;

public enum OperationKind { Initialize, Backup, Snapshots, Check, Restore, Reconcile }

public enum OperationResult { Succeeded, Failed, Cancelled }

public sealed record EngineIdentity(string Name, string Version, string Sha256);

public sealed record ReceiptSource(string Kind, string StableId);

/// <summary>
/// The machine-readable record of one engine operation, as defined by
/// docs/11-executable-prototype.md. It contains no secrets and is never required to restore data:
/// a repository stays recoverable without any receipt.
/// </summary>
public sealed record OperationReceipt(
    Guid OperationId,
    OperationKind Operation,
    string RepositoryId,
    EngineIdentity Engine,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    OperationResult Result,
    string? SnapshotId,
    ReceiptSource? Source,
    IReadOnlyDictionary<string, long> Metrics,
    IReadOnlyList<string> Warnings)
{
    public const string Schema = "fortiq.operation-receipt";
    public const int SchemaVersion = 1;
}

public interface IOperationReceiptStore
{
    /// <summary>Persists a receipt and returns the location it was written to.</summary>
    Task<string> SaveAsync(OperationReceipt receipt, CancellationToken cancellationToken);
}
