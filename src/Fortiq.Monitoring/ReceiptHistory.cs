using System.Text.Json;

namespace Fortiq.Monitoring;

/// <summary>What the receipts say happened to one repository, most recent of each kind.</summary>
public sealed record RepositoryEvidence(
    string RepositoryId,
    DateTimeOffset? LastBackupAt,
    DateTimeOffset? LastHealthyCheckAt,
    DateTimeOffset? LastProvenRestoreAt,
    string? LastFailure);

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
        private DateTimeOffset? _backup;
        private DateTimeOffset? _check;
        private DateTimeOffset? _restore;
        private (DateTimeOffset At, string Message)? _failure;

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
                    ? warnings.EnumerateArray().FirstOrDefault().GetString()
                    : null;

                if (_failure is null || at > _failure.Value.At)
                {
                    _failure = (at, message ?? $"The last {operation} did not succeed.");
                }

                return;
            }

            switch (operation)
            {
                case "backup":
                    _backup = Later(_backup, at);
                    break;
                case "check":
                    _check = Later(_check, at);
                    break;
                case "restore":
                    _restore = Later(_restore, at);
                    break;
                default:
                    break;
            }
        }

        internal RepositoryEvidence ToEvidence(string repositoryId)
        {
            // A failure older than the last success is history, not a current problem.
            var newestSuccess = new[] { _backup, _check, _restore }.Where(at => at is not null).Max();
            var failure = _failure is { } recorded && (newestSuccess is null || recorded.At > newestSuccess)
                ? recorded.Message
                : null;

            return new RepositoryEvidence(repositoryId, _backup, _check, _restore, failure);
        }

        private static DateTimeOffset Later(DateTimeOffset? current, DateTimeOffset candidate) =>
            current is null || candidate > current ? candidate : current.Value;
    }
}
