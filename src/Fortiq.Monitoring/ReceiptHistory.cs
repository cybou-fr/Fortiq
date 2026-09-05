using System.Text.Json;

namespace Fortiq.Monitoring;

/// <summary>What the receipts say happened to one repository, most recent of each kind.</summary>
public sealed record RepositoryEvidence(
    string RepositoryId,
    DateTimeOffset? LastBackupAt,
    DateTimeOffset? LastHealthyCheckAt,
    DateTimeOffset? LastProvenRestoreAt,
    string? LastFailure,
    /// <summary>Successful backups newest first, for comparing one against the rest.</summary>
    IReadOnlyList<BackupObservation>? Backups = null)
{
    public IReadOnlyList<BackupObservation> Backups { get; init; } = Backups ?? [];
}

/// <summary>
/// Reads the operation receipts a machine has kept and summarises them per repository. Receipts are
/// evidence of what happened; this is where monitoring gets its facts instead of inventing them.
/// </summary>
/// <remarks>
/// Only successful operations count towards recency. A check that failed does not make a repository
/// checked, and a restore that failed proves nothing about recovery - counting either would turn
/// monitoring into a report on how often Fortiq ran rather than on whether the data comes back.
/// </remarks>
public static class ReceiptHistory
{
    public static async Task<IReadOnlyList<RepositoryEvidence>> ReadAsync(
        string receiptDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(receiptDirectory);
        if (!Directory.Exists(receiptDirectory))
        {
            return [];
        }

        var perRepository = new Dictionary<string, Accumulator>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in Directory.EnumerateFiles(receiptDirectory, "*.json", SearchOption.AllDirectories))
        {
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(await File.ReadAllTextAsync(path, cancellationToken));
            }
            catch (JsonException)
            {
                // A receipt that cannot be read is not evidence of anything; monitoring must not stop
                // because one file on disk is damaged.
                continue;
            }

            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || root.TryGetProperty("schema", out var schema) is false
                    || schema.GetString() != "fortiq.operation-receipt")
                {
                    continue;
                }

                var repository = root.TryGetProperty("repositoryId", out var id) ? id.GetString() : null;
                if (string.IsNullOrEmpty(repository))
                {
                    continue;
                }

                if (!perRepository.TryGetValue(repository, out var accumulator))
                {
                    accumulator = new Accumulator();
                    perRepository[repository] = accumulator;
                }

                accumulator.Add(root);
            }
        }

        return [.. perRepository.Select(pair => pair.Value.ToEvidence(pair.Key))];
    }

    private sealed class Accumulator
    {
        private readonly List<BackupObservation> _backups = [];
        private DateTimeOffset? _backup;
        private DateTimeOffset? _check;
        private DateTimeOffset? _restore;
        private readonly Dictionary<string, (DateTimeOffset At, string Message)> _failures = new(StringComparer.Ordinal);
        private readonly Dictionary<string, DateTimeOffset> _successes = new(StringComparer.Ordinal);

        internal void Add(JsonElement receipt)
        {
            var operation = receipt.TryGetProperty("operation", out var kind) ? kind.GetString() : null;
            var result = receipt.TryGetProperty("engineResult", out var engine) ? engine.GetString() : null;
            if (!receipt.TryGetProperty("completedAt", out var completed)
                || !completed.TryGetDateTimeOffset(out var at))
            {
                return;
            }

            if (result != "succeeded")
            {
                var message = receipt.TryGetProperty("warnings", out var warnings) && warnings.ValueKind == JsonValueKind.Array
                    && warnings.GetArrayLength() > 0 ? warnings[0].GetString()
                    : null;

                var operationKey = operation ?? "unknown";
                if (!_failures.TryGetValue(operationKey, out var previous) || at > previous.At)
                {
                    _failures[operationKey] = (at, message ?? $"The last {operation} did not succeed.");
                }

                return;
            }

            if (operation is not null)
            {
                _successes[operation] = _successes.TryGetValue(operation, out var previous)
                    && previous > at ? previous : at;
            }

            switch (operation)
            {
                case "backup":
                    _backup = Later(_backup, at);
                    _backups.Add(new BackupObservation(
                        at,
                        receipt.TryGetProperty("snapshotId", out var snapshot) ? snapshot.GetString() : null,
                        Metric(receipt, "bytesProcessed"),
                        Metric(receipt, "bytesAdded"),
                        Metric(receipt, "filesChanged")));
                    break;
                case "check":
                    _check = Later(_check, at);
                    break;
                case "restoreProof":
                    _restore = Later(_restore, at);
                    break;
                default:
                    break;
            }
        }

        internal RepositoryEvidence ToEvidence(string repositoryId)
        {
            // A successful backup cannot clear a failed check or failed restore verification.
            var failure = _failures
                .Where(pair => !_successes.TryGetValue(pair.Key, out var success) || pair.Value.At >= success)
                .OrderByDescending(pair => pair.Value.At)
                .Select(pair => pair.Value.Message)
                .FirstOrDefault();

            return new RepositoryEvidence(repositoryId, _backup, _check, _restore, failure)
            {
                Backups = [.. _backups.OrderByDescending(backup => backup.CompletedAt)]
            };
        }

        /// <summary>
        /// Reads one figure a backup recorded. A receipt written before a metric existed simply does
        /// not carry it, and zero is the honest reading: nothing was recorded, so nothing is claimed.
        /// </summary>
        private static long Metric(JsonElement receipt, string name) =>
            receipt.TryGetProperty("metrics", out var metrics)
            && metrics.ValueKind == JsonValueKind.Object
            && metrics.TryGetProperty(name, out var value)
            && value.TryGetInt64(out var number)
                ? number
                : 0;

        private static DateTimeOffset Later(DateTimeOffset? current, DateTimeOffset candidate) =>
            current is null || candidate > current ? candidate : current.Value;
    }
}
