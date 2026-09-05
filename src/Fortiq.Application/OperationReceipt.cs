namespace Fortiq.Application;

public enum OperationKind { Initialize, Backup, Snapshots, Check, Restore, Reconcile, Retention, RestoreProof }

/// <summary>What the engine itself did. It is not affected by what the caller did afterwards.</summary>
public enum EngineResult { Succeeded, Failed, Cancelled }

/// <summary>Whether the evidence for an operation could be persisted.</summary>
public enum EvidenceWriteResult { Succeeded, Failed }

public sealed record EngineIdentity(string Name, string Version, string Sha256);

/// <summary>
/// The source an operation acted on, and how it was read. Consistency is part of the evidence: a
/// live backup and a point-in-time one are different facts about the same directory.
/// </summary>
public sealed record ReceiptSource(string Kind, string StableId, string? Consistency = null);

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
    EngineResult EngineResult,
    string? SnapshotId,
    ReceiptSource? Source,
    IReadOnlyDictionary<string, long> Metrics,
    IReadOnlyList<string> Warnings)
{
    public const string Schema = "fortiq.operation-receipt";
    public const int SchemaVersion = 1;
}

/// <summary>
/// The outcome of one operation as a whole: what the engine did, and whether the evidence for it
/// reached storage. The two are reported separately because a lost receipt does not change what the
/// engine did, and a successful engine run does not prove its evidence was kept.
/// </summary>
public sealed record OperationEvidence(
    OperationReceipt Receipt,
    EvidenceWriteResult WriteResult,
    string? Location,
    Exception? WriteError);

/// <summary>Receives the evidence of every completed operation, successful or not.</summary>
public interface IOperationEvidenceObserver
{
    void OnEvidence(OperationEvidence evidence);
}

public interface IOperationReceiptStore
{
    /// <summary>Persists a receipt and returns the location it was written to.</summary>
    Task<string> SaveAsync(OperationReceipt receipt, CancellationToken cancellationToken);
}
