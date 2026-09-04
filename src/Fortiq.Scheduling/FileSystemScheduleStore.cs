using System.Text.Json;
using System.Text.Json.Nodes;
using Fortiq.Application;

namespace Fortiq.Scheduling;

/// <summary>Where schedules and their history live.</summary>
public interface IScheduleStore
{
    Task<IReadOnlyList<BackupSchedule>> ReadSchedulesAsync(CancellationToken cancellationToken);

    Task<ScheduleState> ReadStateAsync(string scheduleId, CancellationToken cancellationToken);

    Task WriteStateAsync(ScheduleState state, CancellationToken cancellationToken);
}

/// <summary>A schedule file that could not safely participate in the latest read.</summary>
public sealed record ScheduleLoadIssue(string FileName, string Failure);

/// <summary>Optional diagnostics exposed by stores that load independent schedule documents.</summary>
public interface IScheduleIssueSource
{
    IReadOnlyList<ScheduleLoadIssue> LastReadIssues { get; }
}

/// <summary>
/// Schedules as files a person can read and edit, and state as files Fortiq writes. They are kept
/// apart deliberately: configuration is not history, and writing history must never rewrite what
/// someone configured.
/// </summary>
/// <remarks>
/// The recurrence is serialised through an explicit, closed shape rather than a type discriminator:
/// a schedule file decides what code runs and when, so it may name only the kinds this build knows.
/// </remarks>
public sealed class FileSystemScheduleStore : IScheduleStore, IScheduleIssueSource
{
    private const string Schema = "fortiq.backup-schedule";
    private const int Version = 1;

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly string _schedules;
    private readonly string _state;
    private IReadOnlyList<ScheduleLoadIssue> _lastReadIssues = [];

    public FileSystemScheduleStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var root = Path.GetFullPath(directory);
        _schedules = Path.Combine(root, "schedules");
        _state = Path.Combine(root, "state");
    }

    public IReadOnlyList<ScheduleLoadIssue> LastReadIssues => _lastReadIssues;

    public async Task<IReadOnlyList<BackupSchedule>> ReadSchedulesAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_schedules))
        {
            _lastReadIssues = [];
            return [];
        }

        var schedules = new List<BackupSchedule>();
        var issues = new List<ScheduleLoadIssue>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(_schedules, "*.json").Order(StringComparer.Ordinal))
        {
            var fileName = Path.GetFileName(path);
            try
            {
                var document = JsonNode.Parse(await File.ReadAllTextAsync(path, cancellationToken))
                    ?? throw new InvalidDataException($"The schedule in {fileName} is empty.");
                var schedule = ReadSchedule(document, fileName);
                if (!ids.Add(schedule.Id))
                {
                    throw new InvalidDataException($"Schedule ID '{schedule.Id}' is duplicated by {fileName}.");
                }

                schedules.Add(schedule);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // Configuration files are independent failure domains. Keep the diagnostic, but do
                // not let one hand-edited or partially copied file suppress every healthy backup.
                issues.Add(new ScheduleLoadIssue(fileName, error.Message));
            }
        }

        _lastReadIssues = issues.ToArray();
        return schedules;
    }

    public async Task<ScheduleState> ReadStateAsync(string scheduleId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scheduleId);
        var path = StatePath(scheduleId);
        if (!File.Exists(path))
        {
            return new ScheduleState(scheduleId);
        }

        var document = JsonNode.Parse(await File.ReadAllTextAsync(path, cancellationToken))
            ?? throw new InvalidDataException("The schedule state is empty.");

        return new ScheduleState(
            scheduleId,
            Time(document["lastAttemptAt"]),
            Time(document["lastSuccessAt"]),
            document["lastSnapshotId"]?.GetValue<string>(),
            document["lastFailure"]?.GetValue<string>());
    }

    public async Task WriteStateAsync(ScheduleState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        Directory.CreateDirectory(_state);

        var document = new JsonObject
        {
            ["schema"] = "fortiq.schedule-state",
            ["version"] = Version,
            ["scheduleId"] = state.ScheduleId,
            ["lastAttemptAt"] = state.LastAttemptAt?.ToString("O"),
            ["lastSuccessAt"] = state.LastSuccessAt?.ToString("O"),
            ["lastSnapshotId"] = state.LastSnapshotId,
            ["lastFailure"] = state.LastFailure
        };

        // Written whole and moved into place: a state file read halfway through a write would say
        // something about a run that never happened.
        var path = StatePath(state.ScheduleId);
        var temporary = path + ".partial";
        await File.WriteAllTextAsync(temporary, document.ToJsonString(Options), cancellationToken);
        File.Move(temporary, path, overwrite: true);
    }

    private string StatePath(string scheduleId)
    {
        if (!SafeId(scheduleId))
        {
            throw new InvalidDataException("A schedule ID must be letters, digits, '.', '_' or '-'.");
        }

        return Path.Combine(_state, scheduleId + ".json");
    }

    private static bool SafeId(string value) =>
        value.Length is > 0 and <= 128
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');

    private static DateTimeOffset? Time(JsonNode? node) =>
        node is null ? null : DateTimeOffset.Parse(node.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture);

    private static BackupSchedule ReadSchedule(JsonNode document, string fileName)
    {
        if (document["schema"]?.GetValue<string>() != Schema || document["version"]?.GetValue<int>() != Version)
        {
            throw new InvalidDataException($"Unsupported schedule schema or version in {fileName}.");
        }

        var id = Required(document, "id", fileName);
        if (!SafeId(id))
        {
            throw new InvalidDataException($"The schedule ID in {fileName} is not a usable identifier.");
        }

        return new BackupSchedule(
            id,
            Required(document, "repository", fileName),
            Required(document, "kit", fileName),
            Required(document, "source", fileName),
            Required(document, "sourceStableId", fileName),
            ReadRecurrence(document["recurrence"], fileName),
            document["consistency"]?.GetValue<string>() switch
            {
                "snapshot" => SourceConsistency.FileSystemSnapshot,
                "live" or null => SourceConsistency.Live,
                _ => throw new InvalidDataException($"Unknown consistency in {fileName}.")
            },
            document["catchUp"]?.GetValue<string>() switch
            {
                "skip" => CatchUp.Skip,
                "once" or null => CatchUp.Once,
                _ => throw new InvalidDataException($"Unknown catch-up policy in {fileName}.")
            },
            document["enabled"]?.GetValue<bool>() ?? true,
            // Absent means no drills. A restore drill is a full restore of the source, so it is
            // never turned on by a schedule file that does not ask for it.
            document["drillRecurrence"] is { } drill ? ReadRecurrence(drill, fileName) : null);
    }

    private static Recurrence ReadRecurrence(JsonNode? node, string fileName)
    {
        var kind = node?["kind"]?.GetValue<string>()
            ?? throw new InvalidDataException($"The schedule in {fileName} has no recurrence.");

        switch (kind)
        {
            case "interval":
                var period = TimeSpan.Parse(
                    node!["period"]!.GetValue<string>(),
                    System.Globalization.CultureInfo.InvariantCulture);
                return new EveryInterval(period);

            case "dailyAt":
                var time = TimeOnly.Parse(
                    node!["timeOfDay"]!.GetValue<string>(),
                    System.Globalization.CultureInfo.InvariantCulture);
                var zoneId = node["timeZone"]?.GetValue<string>();
                var zone = zoneId is null ? TimeZoneInfo.Local : TimeZoneInfo.FindSystemTimeZoneById(zoneId);
                var days = node["days"]?.AsArray()
                    .Select(day => Enum.Parse<DayOfWeek>(day!.GetValue<string>(), ignoreCase: true))
                    .ToArray();

                return new DailyAt(time, zone, days);

            default:
                // A schedule file decides what runs and when; an unknown kind is refused rather than
                // approximated by the nearest one this build understands.
                throw new InvalidDataException($"Unknown recurrence kind '{kind}' in {fileName}.");
        }
    }

    private static string Required(JsonNode document, string name, string fileName) =>
        document[name]?.GetValue<string>()
        ?? throw new InvalidDataException($"The schedule in {fileName} is missing '{name}'.");
}
