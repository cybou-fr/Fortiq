namespace Fortiq.Application;

public enum OperationKind { Initialize, Backup, Snapshots, Files, Check, Restore, Reconcile, Retention, RestoreProof }

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
/// docs/11-executable-prototype.md and ADR-007. It contains no secrets and is never required to restore data.
/// Chained via SHA-256 hash chaining to form an unbroken, tamper-evident audit ledger.
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
    IReadOnlyList<string> Warnings,
    long SequenceNumber = 0,
    string? PreviousReceiptHash = null,
    string? ReceiptHash = null,
    int Version = OperationReceipt.SchemaVersion)
{
    public const string Schema = "fortiq.operation-receipt";
    public const int SchemaVersion = 2;
    public const string GenesisHash = "GENESIS";

    /// <summary>
    /// Computes deterministic SHA-256 digest over canonical receipt contents, chaining to the previous receipt hash.
    /// </summary>
    public static string ComputeCanonicalHash(
        Guid operationId,
        OperationKind operation,
        string repositoryId,
        EngineIdentity engine,
        DateTimeOffset startedAt,
        DateTimeOffset completedAt,
        EngineResult engineResult,
        string? snapshotId,
        ReceiptSource? source,
        IReadOnlyDictionary<string, long> metrics,
        IReadOnlyList<string> warnings,
        long sequenceNumber,
        string previousReceiptHash,
        int version = SchemaVersion)
    {
        using var stream = new System.IO.MemoryStream();
        using var writer = new System.Text.Json.Utf8JsonWriter(stream);

        writer.WriteStartObject();
        writer.WriteString("schema", Schema);
        writer.WriteNumber("version", version);
        writer.WriteString("operationId", operationId);
        writer.WriteString("operation", operation.ToString());
        writer.WriteString("repositoryId", repositoryId);
        writer.WriteString("engineName", engine.Name);
        writer.WriteString("engineVersion", engine.Version);
        writer.WriteString("engineSha256", engine.Sha256);
        writer.WriteString("startedAt", startedAt.ToUniversalTime().ToString("O"));
        writer.WriteString("completedAt", completedAt.ToUniversalTime().ToString("O"));
        writer.WriteString("engineResult", engineResult.ToString());
        if (snapshotId is not null) writer.WriteString("snapshotId", snapshotId);
        if (source is not null)
        {
            writer.WriteString("sourceKind", source.Kind);
            writer.WriteString("sourceStableId", source.StableId);
            if (source.Consistency is not null) writer.WriteString("sourceConsistency", source.Consistency);
        }
        writer.WriteNumber("sequenceNumber", sequenceNumber);
        writer.WriteString("previousReceiptHash", previousReceiptHash);

        writer.WriteStartArray("metrics");
        foreach (var (k, v) in metrics.OrderBy(m => m.Key, StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            writer.WriteString("key", k);
            writer.WriteNumber("val", v);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();

        writer.WriteStartArray("warnings");
        foreach (var w in warnings) writer.WriteStringValue(w);
        writer.WriteEndArray();

        writer.WriteEndObject();
        writer.Flush();

        var hashBytes = System.Security.Cryptography.SHA256.HashData(stream.ToArray());
        return Convert.ToHexStringLower(hashBytes);
    }
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
