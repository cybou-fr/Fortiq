using System.Text.Json;

namespace Fortiq.Monitoring;

/// <summary>What one receipt says happened, as a screen shows it.</summary>
/// <param name="CompletedAt">When the operation finished.</param>
/// <param name="RepositoryId">Which repository it was about.</param>
/// <param name="Operation">The operation as the receipt names it: backup, check, restoreProof, retention.</param>
/// <param name="Succeeded">Whether the engine reported success.</param>
/// <param name="SnapshotId">The snapshot a backup or a proof was about, when there is one.</param>
/// <param name="Detail">The first warning a failed operation carried, when it carried one.</param>
/// <param name="Verifiable">
/// False for a receipt written before the chained schema. Such a file carries no sequence number, no
/// previous hash and no hash of its own, so a claim that a restore succeeded cannot be told apart from
/// one somebody typed. It is shown, and shown as unverifiable, because a repository whose only history
/// is unverifiable has a different problem from one with no history at all.
/// </param>
public sealed record ReceiptEvent(
    DateTimeOffset CompletedAt,
    string RepositoryId,
    string Operation,
    bool Succeeded,
    string? SnapshotId,
    string? Detail,
    bool Verifiable);

/// <summary>
/// The receipts as a list of events, newest first - the history itself rather than a summary of it.
/// </summary>
/// <remarks>
/// <see cref="ReceiptHistory"/> answers what monitoring needs: the most recent of each kind, per
/// repository, because a verdict is about the present. A person looking at a history needs the
/// opposite - every attempt in order, the failures included, because the failure that was followed by
/// a success is exactly the thing a summary is designed to drop.
///
/// The desktop showed three synthesised rows per repository, built from the same three timestamps the
/// dashboard already displayed. A failed drill, a backup that could not reach its repository, a
/// retention run that removed snapshots: none of them appeared anywhere, though every one had been
/// written to disk and chained.
/// </remarks>
public static class ReceiptTimeline
{
    /// <summary>How many events are read. A history screen is not an archive reader.</summary>
    public const int DefaultLimit = 250;

    public static async Task<IReadOnlyList<ReceiptEvent>> ReadAsync(
        string receiptDirectory,
        CancellationToken cancellationToken,
        int limit = DefaultLimit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(receiptDirectory);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        if (!Directory.Exists(receiptDirectory))
        {
            return [];
        }

        var events = new List<ReceiptEvent>();

        foreach (var path in Directory.EnumerateFiles(receiptDirectory, "*.json", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(await File.ReadAllTextAsync(path, cancellationToken));
            }
            catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
            {
                // One damaged or unreadable file is not a reason to show no history at all.
                continue;
            }

            using (document)
            {
                if (Read(document.RootElement) is { } entry)
                {
                    events.Add(entry);
                }
            }
        }

        return [.. events.OrderByDescending(entry => entry.CompletedAt).Take(limit)];
    }

    private static ReceiptEvent? Read(JsonElement receipt)
    {
        if (receipt.ValueKind != JsonValueKind.Object
            || !receipt.TryGetProperty("schema", out var schema)
            || schema.GetString() != "fortiq.operation-receipt")
        {
            return null;
        }

        var repository = receipt.TryGetProperty("repositoryId", out var id) ? id.GetString() : null;
        if (string.IsNullOrEmpty(repository))
        {
            return null;
        }

        if (!receipt.TryGetProperty("completedAt", out var completed)
            || !completed.TryGetDateTimeOffset(out var at))
        {
            // A receipt with no time cannot take a place in a list ordered by time.
            return null;
        }

        var version = receipt.TryGetProperty("version", out var declared) && declared.TryGetInt32(out var number)
            ? number
            : 1;

        var succeeded = receipt.TryGetProperty("engineResult", out var engine) && engine.GetString() == "succeeded";

        return new ReceiptEvent(
            at,
            repository,
            receipt.TryGetProperty("operation", out var kind) ? kind.GetString() ?? "unknown" : "unknown",
            succeeded,
            receipt.TryGetProperty("snapshotId", out var snapshot) ? snapshot.GetString() : null,
            Warning(receipt),
            version >= 2);
    }

    private static string? Warning(JsonElement receipt) =>
        receipt.TryGetProperty("warnings", out var warnings)
        && warnings.ValueKind == JsonValueKind.Array
        && warnings.GetArrayLength() > 0
            ? warnings[0].GetString()
            : null;
}
