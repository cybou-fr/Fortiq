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

/// <summary>
/// The parts of a schedule a person changes from the application, and nothing else.
/// </summary>
/// <remarks>
/// Deliberately not the whole <see cref="BackupSchedule"/>. The repository, the kit and the source
/// are what the schedule is about: changing them here would not move a backup, it would point the
/// same history at different data, and the honest way to protect a different folder is to protect a
/// different folder. What is left is when it runs, whether it runs, how often recovery is proven, and
/// what may be forgotten.
/// </remarks>
/// <param name="Enabled">False pauses the schedule without forgetting anything about it.</param>
/// <param name="BackupTime">The time of day the daily backup runs, in this machine's time zone.</param>
/// <param name="DrillEvery">How often recovery is proven unattended. Null turns drills off.</param>
/// <param name="Retention">What to keep. Null keeps everything, forever, which is the default.</param>
/// <param name="Prune">Whether forgotten snapshots also have their data removed from the repository.</param>
public sealed record SchedulePreferences(
    bool Enabled,
    TimeOnly BackupTime,
    TimeSpan? DrillEvery,
    RetentionPolicy? Retention,
    PruneMode Prune);

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

    /// <summary>
    /// Applies the settings somebody chose to an existing schedule file.
    /// </summary>
    /// <remarks>
    /// The document on disk is edited rather than replaced. A schedule file is something a person may
    /// have written or extended by hand - a weekday list, a field a later version will understand -
    /// and rewriting it from the fields this screen knows about would silently discard the rest.
    ///
    /// Written whole and moved into place, like the state file: a schedule read halfway through a
    /// write is a schedule with no recurrence, and the reader would report the file as broken.
    /// </remarks>
    public async Task UpdateAsync(string scheduleId, SchedulePreferences preferences, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scheduleId);
        ArgumentNullException.ThrowIfNull(preferences);

        if (preferences.Retention is { } requested && !requested.KeepsSomething)
        {
            // The same refusal the reader makes, made before anything is written. A policy that keeps
            // nothing is an instruction to delete every backup, and it must never be arrived at by a
            // form somebody left empty.
            throw new InvalidDataException(
                "A retention policy that keeps nothing is deletion, not retention. Leave retention off to keep everything.");
        }

        var path = SchedulePath(scheduleId);
        var document = JsonNode.Parse(await File.ReadAllTextAsync(path, cancellationToken))
            ?? throw new InvalidDataException($"The schedule in {Path.GetFileName(path)} is empty.");

        document["enabled"] = preferences.Enabled;

        // The recurrence is replaced rather than edited in place: switching a schedule from an
        // interval to a daily time would otherwise leave the interval's period beside the new fields,
        // and the reader takes the kind at its word.
        var recurrence = new JsonObject
        {
            ["kind"] = "dailyAt",
            ["timeOfDay"] = preferences.BackupTime.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture),
            ["timeZone"] = TimeZoneInfo.Local.Id
        };

        // A weekday restriction somebody wrote by hand is theirs, and the screen has no field for it.
        if (document["recurrence"] is JsonNode existing
            && existing["kind"]?.GetValue<string>() == "dailyAt"
            && existing["days"] is JsonArray days)
        {
            recurrence["days"] = days.DeepClone();
        }

        document["recurrence"] = recurrence;

        if (preferences.DrillEvery is { } drill)
        {
            document["drillRecurrence"] = new JsonObject
            {
                ["kind"] = "interval",
                ["period"] = drill.ToString("c", System.Globalization.CultureInfo.InvariantCulture)
            };
        }
        else
        {
            document.AsObject().Remove("drillRecurrence");
        }

        if (preferences.Retention is { } retention)
        {
            // Retention needs both halves. The recurrence is put beside the policy here so a screen
            // cannot produce the one shape the reader treats as unconfigured.
            document["retentionRecurrence"] = new JsonObject
            {
                ["kind"] = "interval",
                ["period"] = TimeSpan.FromDays(1).ToString("c", System.Globalization.CultureInfo.InvariantCulture)
            };

            var policy = new JsonObject();
            if (retention.KeepLast is { } last) policy["keepLast"] = last;
            if (retention.KeepDaily is { } daily) policy["keepDaily"] = daily;
            if (retention.KeepWeekly is { } weekly) policy["keepWeekly"] = weekly;
            if (retention.KeepMonthly is { } monthly) policy["keepMonthly"] = monthly;
            if (retention.KeepYearly is { } yearly) policy["keepYearly"] = yearly;
            if (retention.KeepWithin is { } within)
            {
                policy["keepWithin"] = within.ToString("c", System.Globalization.CultureInfo.InvariantCulture);
            }

            document["retention"] = policy;
            document["prune"] = preferences.Prune == PruneMode.ForgetAndPrune;
        }
        else
        {
            // Both halves go, so what is left cannot be read as retention by any later version.
            document.AsObject().Remove("retentionRecurrence");
            document.AsObject().Remove("retention");
            document.AsObject().Remove("prune");
        }

        // Read back before it is committed. The reader is the authority on what a schedule file means,
        // and a document this method could write but the service could not read would stop the backups
        // it was meant to adjust.
        _ = ReadSchedule(document, Path.GetFileName(path));

        var temporary = path + ".partial";
        await File.WriteAllTextAsync(temporary, document.ToJsonString(Options), cancellationToken);
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>
    /// Stops protecting a source: the schedule and its histories go, and nothing else does.
    /// </summary>
    /// <remarks>
    /// The repository, the recovery kit and the receipts are left exactly where they are. Somebody who
    /// stops backing a folder up has not asked to lose the backups they already have, and a product
    /// whose whole claim is that data comes back must not be the thing that deletes it. What was
    /// backed up stays openable with the recovery kit and the 24 words for as long as it exists.
    /// </remarks>
    public Task RemoveAsync(string scheduleId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scheduleId);
        cancellationToken.ThrowIfCancellationRequested();

        var path = SchedulePath(scheduleId);
        File.Delete(path);

        // The backup's history and the drill's and retention's alongside it. Left behind, they would
        // be adopted by the next schedule given the same id, which would start life believing it had
        // already run.
        foreach (var stateId in new[] { scheduleId, scheduleId + ".drill", scheduleId + ".retention" })
        {
            var statePath = Path.Combine(_state, stateId + ".json");
            if (File.Exists(statePath))
            {
                File.Delete(statePath);
            }
        }

        return Task.CompletedTask;
    }

    private string SchedulePath(string scheduleId)
    {
        if (!SafeId(scheduleId))
        {
            throw new InvalidDataException("A schedule ID must be letters, digits, '.', '_' or '-'.");
        }

        var path = Path.Combine(_schedules, scheduleId + ".json");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"No schedule on this machine has the id '{scheduleId}'.", path);
        }

        return path;
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
            document["drillRecurrence"] is { } drill ? ReadRecurrence(drill, fileName) : null,
            // Retention deletes backups, so both halves must be present and the policy must keep
            // something. A recurrence on its own is a schedule for deleting snapshots by no rule.
            document["retentionRecurrence"] is { } retention ? ReadRecurrence(retention, fileName) : null,
            ReadRetention(document["retention"], fileName),
            document["prune"]?.GetValue<bool>() == true ? PruneMode.ForgetAndPrune : PruneMode.ForgetOnly);
    }

    private static RetentionPolicy? ReadRetention(JsonNode? node, string fileName)
    {
        if (node is null)
        {
            return null;
        }

        var policy = new RetentionPolicy(
            KeepLast: (int?)node["keepLast"],
            KeepDaily: (int?)node["keepDaily"],
            KeepWeekly: (int?)node["keepWeekly"],
            KeepMonthly: (int?)node["keepMonthly"],
            KeepYearly: (int?)node["keepYearly"],
            KeepWithin: node["keepWithin"] is { } within
                ? TimeSpan.Parse(within.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture)
                : null);

        // Refused rather than ignored. A policy that keeps nothing is not a conservative reading of
        // a typo; it is an instruction to delete every backup, and it must never be arrived at by
        // accident.
        return policy.KeepsSomething
            ? policy
            : throw new InvalidDataException(
                $"The retention policy in {fileName} keeps nothing. A policy that keeps nothing is deletion, not retention.");
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

    /// <summary>
    /// Writes a default backup and restore drill schedule file for a newly provisioned repository.
    /// </summary>
    public static async Task WriteDefaultScheduleAsync(
        string directory,
        string repositoryId,
        string repositoryLocation,
        string kitDirectory,
        string sourcePath,
        TimeOnly nightly,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryId);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryLocation);
        ArgumentException.ThrowIfNullOrWhiteSpace(kitDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        Directory.CreateDirectory(directory);

        var document = new JsonObject
        {
            ["schema"] = Schema,
            ["version"] = Version,
            ["id"] = repositoryId,
            ["repository"] = repositoryLocation,
            ["kit"] = Path.GetFullPath(kitDirectory),
            ["source"] = Path.GetFullPath(sourcePath),
            ["sourceStableId"] = Path.GetFullPath(sourcePath),
            ["recurrence"] = new JsonObject
            {
                ["kind"] = "dailyAt",
                ["timeOfDay"] = nightly.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture),
                ["timeZone"] = TimeZoneInfo.Local.Id
            },
            ["drillRecurrence"] = new JsonObject
            {
                ["kind"] = "interval",
                ["period"] = "7.00:00:00"
            },
            ["consistency"] = "live",
            ["catchUp"] = "once",
            ["enabled"] = true
        };

        var path = Path.Combine(directory, repositoryId + ".json");
        var temporary = path + ".partial";
        await File.WriteAllTextAsync(
            temporary,
            document.ToJsonString(Options),
            cancellationToken);

        File.Move(temporary, path, overwrite: true);
    }
}
