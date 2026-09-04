using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fortiq.Monitoring;

/// <summary>
/// Writes a health report where something else can read it: a JSON file for people and tools, and a
/// Prometheus text file for a scraper. Neither needs Fortiq to be running or reachable, which is the
/// point - a monitoring path that depends on the thing it monitors reports health right up until it
/// cannot report at all.
/// </summary>
public static class HealthPublication
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static async Task WriteJsonAsync(HealthReport report, string path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var document = new ReportDocument(
            HealthReport.Schema,
            HealthReport.SchemaVersion,
            report.ProducedAt,
            report.Worst,
            report.Repositories);

        await WriteAtomicallyAsync(path, JsonSerializer.Serialize(document, SerializerOptions), cancellationToken);
    }

    /// <summary>
    /// Renders the report in the Prometheus text format, which a node exporter's textfile collector
    /// picks up from disk. Ages are exposed as seconds since the event, so an alert can be written
    /// about staleness without knowing Fortiq's thresholds.
    /// </summary>
    public static string ToPrometheusText(HealthReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var text = new StringBuilder();

        text.AppendLine("# HELP fortiq_repository_recoverable Whether Fortiq can currently claim this repository is recoverable.");
        text.AppendLine("# TYPE fortiq_repository_recoverable gauge");
        foreach (var repository in report.Repositories)
        {
            text.Append(CultureInfo.InvariantCulture, $"fortiq_repository_recoverable{Labels(repository)} ");
            text.AppendLine(repository.Verdict == HealthVerdict.Recoverable ? "1" : "0");
        }

        AppendAge(text, report, "fortiq_repository_last_backup_age_seconds", "Seconds since the last successful backup.",
            repository => repository.Facts.LastBackupAt);
        AppendAge(text, report, "fortiq_repository_last_check_age_seconds", "Seconds since the last healthy integrity check.",
            repository => repository.Facts.LastHealthyCheckAt);
        AppendAge(text, report, "fortiq_repository_last_restore_proof_age_seconds", "Seconds since a restore last proved recovery works.",
            repository => repository.Facts.LastProvenRestoreAt);

        text.AppendLine("# HELP fortiq_repository_storage_immutable Whether the storage keeps what is written to it.");
        text.AppendLine("# TYPE fortiq_repository_storage_immutable gauge");
        foreach (var repository in report.Repositories)
        {
            text.Append(CultureInfo.InvariantCulture, $"fortiq_repository_storage_immutable{Labels(repository)} ");
            text.AppendLine(repository.Facts.StorageImmutable ? "1" : "0");
        }

        return text.ToString();
    }

    public static Task WritePrometheusAsync(HealthReport report, string path, CancellationToken cancellationToken) =>
        WriteAtomicallyAsync(path, ToPrometheusText(report), cancellationToken);

    private static void AppendAge(
        StringBuilder text,
        HealthReport report,
        string metric,
        string help,
        Func<RepositoryHealth, DateTimeOffset?> select)
    {
        text.AppendLine(CultureInfo.InvariantCulture, $"# HELP {metric} {help}");
        text.AppendLine(CultureInfo.InvariantCulture, $"# TYPE {metric} gauge");

        foreach (var repository in report.Repositories)
        {
            // An event that never happened is left out rather than reported as zero seconds ago,
            // which would read as "just now" - the opposite of the truth.
            if (select(repository) is not { } at)
            {
                continue;
            }

            var seconds = Math.Max(0, (report.ProducedAt - at).TotalSeconds);
            text.AppendLine(CultureInfo.InvariantCulture, $"{metric}{Labels(repository)} {seconds:F0}");
        }
    }

    private static string Labels(RepositoryHealth repository) =>
        $"{{repository=\"{Escape(repository.RepositoryId)}\",schedule=\"{Escape(repository.ScheduleId ?? string.Empty)}\"}}";

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    private static async Task WriteAtomicallyAsync(string path, string content, CancellationToken cancellationToken)
    {
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);

        // Written whole and moved into place: a scraper that read half a file would report a state
        // that never existed.
        var temporary = full + ".partial";
        await File.WriteAllTextAsync(temporary, content, cancellationToken);
        File.Move(temporary, full, overwrite: true);
    }

    private sealed record ReportDocument(
        string Schema,
        int Version,
        DateTimeOffset ProducedAt,
        HealthVerdict Worst,
        IReadOnlyList<RepositoryHealth> Repositories);
}
