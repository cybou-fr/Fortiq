using System.Globalization;
using Fortiq.Application;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Fortiq.Infrastructure.Restic;

public sealed record ResticVersionInfo(string Version, string GoVersion, string OperatingSystem, string Architecture);

public sealed record ResticInitializedRepository(string Id, string Repository);

public sealed record ResticBackupSummary(
    string SnapshotId,
    ulong TotalFilesProcessed,
    ulong TotalBytesProcessed,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt);

public sealed record ResticSnapshot(
    string Id,
    DateTimeOffset Time,
    IReadOnlyList<string> Paths,
    IReadOnlyList<string> Tags,
    string Hostname,
    string ProgramVersion);

public sealed record ResticCheckSummary(long ErrorCount, IReadOnlyList<string> BrokenPacks, bool SuggestRepairIndex, bool SuggestPrune)
{
    public bool IsHealthy => ErrorCount == 0 && BrokenPacks.Count == 0 && !SuggestRepairIndex;
}

public sealed record ResticRepositoryConfig(string Id, int Version);

/// <summary>One group of snapshots restic considered together, and what it decided about them.</summary>
public sealed record ResticForgetGroup(IReadOnlyList<string> Keep, IReadOnlyList<string> Remove);

public sealed record ResticRestoreSummary(ulong TotalFiles, ulong FilesRestored, ulong FilesSkipped, ulong TotalBytes, ulong BytesRestored);

public static partial class ResticJsonParser
{
    private const int WrongPasswordExitCode = 12;

    public static ResticVersionInfo ParseVersion(ResticProcessResult result)
    {
        EnsureSuccessfulExit(result);
        using var document = ParseSingleDocument(result.StandardOutput);
        var root = RequireObject(document.RootElement);
        RequireMessageType(root, "version");
        return new ResticVersionInfo(
            RequireString(root, "version"),
            RequireString(root, "go_version"),
            RequireString(root, "go_os"),
            RequireString(root, "go_arch"));
    }

    public static ResticInitializedRepository ParseInitialized(ResticProcessResult result)
    {
        EnsureSuccessfulExit(result);
        using var document = ParseSingleDocument(result.StandardOutput);
        var root = RequireObject(document.RootElement);
        RequireMessageType(root, "initialized");
        return new ResticInitializedRepository(RequireIdentifier(root, "id"), RequireString(root, "repository"));
    }

    public static ResticBackupSummary ParseBackup(ResticProcessResult result)
    {
        EnsureSuccessfulExit(result);
        using var summary = RequireTerminalMessage(result.StandardOutput, "summary");
        var root = summary.RootElement;
        if (OptionalBoolean(root, "dry_run"))
        {
            throw new InvalidDataException("A dry-run cannot produce a successful backup receipt.");
        }

        return new ResticBackupSummary(
            RequireIdentifier(root, "snapshot_id"),
            RequireUInt64(root, "total_files_processed"),
            RequireUInt64(root, "total_bytes_processed"),
            RequireDateTimeOffset(root, "backup_start"),
            RequireDateTimeOffset(root, "backup_end"));
    }

    public static IReadOnlyList<ResticSnapshot> ParseSnapshots(ResticProcessResult result)
    {
        EnsureSuccessfulExit(result);
        using var document = ParseSingleDocument(result.StandardOutput);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Snapshots output must be a JSON array.");
        }

        var snapshots = new List<ResticSnapshot>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            var root = RequireObject(item);
            snapshots.Add(new ResticSnapshot(
                RequireIdentifier(root, "id"),
                RequireDateTimeOffset(root, "time"),
                RequireStringArray(root, "paths"),
                // Restic omits the field entirely when a snapshot has no tags.
                OptionalStringArray(root, "tags"),
                RequireString(root, "hostname"),
                RequireString(root, "program_version")));
        }

        return snapshots;
    }

    public static ResticCheckSummary ParseCheck(ResticProcessResult result)
    {
        EnsureSuccessfulExit(result);
        using var summary = RequireTerminalMessage(result.StandardOutput, "summary");
        var root = summary.RootElement;
        var parsed = new ResticCheckSummary(
            RequireInt64(root, "num_errors"),
            OptionalStringArray(root, "broken_packs"),
            RequireBoolean(root, "suggest_repair_index"),
            RequireBoolean(root, "suggest_prune"));

        if (!parsed.IsHealthy)
        {
            throw new InvalidDataException("Restic check summary reports repository integrity errors.");
        }

        return parsed;
    }

    /// <summary>
    /// The repository's own identity, as the repository states it. This is what a kit has to be
    /// checked against: a path is not an identity.
    /// </summary>
    public static ResticRepositoryConfig ParseConfig(ResticProcessResult result)
    {
        EnsureSuccessfulExit(result);
        using var document = ParseSingleDocument(result.StandardOutput);
        var root = RequireObject(document.RootElement);
        return new ResticRepositoryConfig(RequireIdentifier(root, "id"), (int)RequireInt64(root, "version"));
    }

    public static ResticRestoreSummary ParseRestore(ResticProcessResult result)
    {
        EnsureSuccessfulExit(result);
        using var summary = RequireTerminalMessage(result.StandardOutput, "summary");
        var root = summary.RootElement;
        return new ResticRestoreSummary(
            RequireUInt64(root, "total_files"),
            RequireUInt64(root, "files_restored"),
            OptionalUInt64(root, "files_skipped"),
            RequireUInt64(root, "total_bytes"),
            RequireUInt64(root, "bytes_restored"));
    }

    /// <summary>
    /// Verifies a lock-removal run. Restic 0.19.1 reports success for <c>unlock --json</c> with an
    /// empty stdout, so a successful exit with no error events is the whole contract.
    /// </summary>
    public static void ParseUnlock(ResticProcessResult result)
    {
        EnsureSuccessfulExit(result);
        foreach (var line in NonEmptyLines(result.StandardOutput))
        {
            using var message = ParseLine(line);
            RequireObject(message.RootElement);
        }
    }

    /// <summary>
    /// Reads what a forget run kept and removed, per group. The same shape comes back for a dry run,
    /// which is what lets the plan be checked before anything is done.
    /// </summary>
    public static IReadOnlyList<ResticForgetGroup> ParseForget(ResticProcessResult result)
    {
        EnsureSuccessfulExit(result);

        // A forget that also prunes prints its plan as JSON and then reports the prune in plain text,
        // so only the first value is read and the rest is left alone.
        using var document = ParseLeadingValue(result.StandardOutput);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Forget output must be a JSON array.");
        }

        var groups = new List<ResticForgetGroup>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            var root = RequireObject(item);
            groups.Add(new ResticForgetGroup(SnapshotIds(root, "keep"), SnapshotIds(root, "remove")));
        }

        return groups;
    }

    private static List<string> SnapshotIds(JsonElement group, string name)
    {
        var ids = new List<string>();
        if (!group.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return ids;
        }

        foreach (var snapshot in array.EnumerateArray())
        {
            ids.Add(RequireIdentifier(RequireObject(snapshot), "id"));
        }

        return ids;
    }

    private static void EnsureSuccessfulExit(ResticProcessResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.ExitCode != 0)
        {
            var diagnostics = DescribeErrors(result.StandardError);

            // Restic reports a repository it cannot decrypt with exit code 12. It is surfaced as one
            // unified failure so a caller cannot tell a wrong secret from a missing key, and so no
            // repository detail travels with the exception.
            if (result.ExitCode == WrongPasswordExitCode
                || diagnostics.Contains("wrong password or no key found", StringComparison.OrdinalIgnoreCase))
            {
                throw new UnlockFailedException();
            }

            throw new InvalidDataException(
                $"Restic operation failed with exit code {result.ExitCode}.{diagnostics}");
        }

        foreach (var line in NonEmptyLines(result.StandardError))
        {
            using var error = ParseLine(line);
            var type = RequireString(RequireObject(error.RootElement), "message_type");
            if (type is "error" or "exit_error")
            {
                throw new InvalidDataException("Restic emitted an error event despite a zero exit code.");
            }
        }
    }

    /// <summary>
    /// Collects the messages restic itself reported so a failure carries actionable diagnostics.
    /// Only engine-provided text is included; Fortiq never passes secrets to the engine command line.
    /// </summary>
    private static string DescribeErrors(string standardError)
    {
        var messages = new List<string>();
        foreach (var line in NonEmptyLines(standardError))
        {
            JsonDocument document;
            try
            {
                document = ParseLine(line);
            }
            catch (InvalidDataException)
            {
                continue;
            }

            using (document)
            {
                if (document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty("message", out var message)
                    && message.ValueKind == JsonValueKind.String)
                {
                    messages.Add(message.GetString()!);
                }
            }
        }

        return messages.Count == 0 ? string.Empty : " " + string.Join(" | ", messages);
    }

    private static JsonDocument RequireTerminalMessage(string jsonLines, string expectedType)
    {
        var lines = NonEmptyLines(jsonLines);
        if (lines.Length == 0)
        {
            throw new InvalidDataException("Restic emitted no JSON messages.");
        }

        for (var index = 0; index < lines.Length - 1; index++)
        {
            using var message = ParseLine(lines[index]);
            if (RequireString(RequireObject(message.RootElement), "message_type") == expectedType)
            {
                throw new InvalidDataException("Terminal message appeared before the end of output.");
            }
        }

        var terminal = ParseLine(lines[^1]);
        try
        {
            RequireMessageType(RequireObject(terminal.RootElement), expectedType);
            return terminal;
        }
        catch
        {
            terminal.Dispose();
            throw;
        }
    }

    private static JsonDocument ParseLeadingValue(string json)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(json);
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { AllowTrailingCommas = false });

        try
        {
            if (JsonDocument.TryParseValue(ref reader, out var document))
            {
                return document;
            }
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("Restic output is not valid JSON.", error);
        }

        throw new InvalidDataException("Restic emitted no JSON value.");
    }

    private static JsonDocument ParseSingleDocument(string json)
    {
        try
        {
            return JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("Restic output is not valid JSON.", error);
        }
    }

    private static JsonDocument ParseLine(string line) => ParseSingleDocument(line);

    private static string[] NonEmptyLines(string value) =>
        value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static JsonElement RequireObject(JsonElement element) => element.ValueKind == JsonValueKind.Object
        ? element
        : throw new InvalidDataException("Restic JSON message must be an object.");

    private static void RequireMessageType(JsonElement element, string expected)
    {
        if (!string.Equals(RequireString(element, "message_type"), expected, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Expected terminal message '{expected}'.");
        }
    }

    private static string RequireIdentifier(JsonElement element, string name)
    {
        var value = RequireString(element, name);
        return IdentifierRegex().IsMatch(value) ? value : throw new InvalidDataException($"Property '{name}' is not a full restic identifier.");
    }

    private static string RequireString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException($"Required string property '{name}' is missing.");
        }

        return property.GetString()!;
    }

    private static ulong RequireUInt64(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.TryGetUInt64(out var value)
            ? value
            : throw new InvalidDataException($"Required unsigned integer property '{name}' is missing.");

    private static ulong OptionalUInt64(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.TryGetUInt64(out var value) ? value : 0;

    private static long RequireInt64(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.TryGetInt64(out var value)
            ? value
            : throw new InvalidDataException($"Required integer property '{name}' is missing.");

    private static bool RequireBoolean(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? property.GetBoolean()
            : throw new InvalidDataException($"Required boolean property '{name}' is missing.");

    private static bool OptionalBoolean(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.ValueKind is JsonValueKind.True or JsonValueKind.False && property.GetBoolean();

    private static DateTimeOffset RequireDateTimeOffset(JsonElement element, string name)
    {
        var text = RequireString(element, name);
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value)
            ? value
            : throw new InvalidDataException($"Property '{name}' is not an RFC3339 timestamp.");
    }

    private static List<string> RequireStringArray(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"Required array property '{name}' is missing.");
        }

        return ParseStringArray(property, name);
    }

    private static List<string> OptionalStringArray(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            return [];
        }

        return property.ValueKind == JsonValueKind.Array
            ? ParseStringArray(property, name)
            : throw new InvalidDataException($"Property '{name}' must be an array or null.");
    }

    private static List<string> ParseStringArray(JsonElement property, string name)
    {
        var values = new List<string>();
        foreach (var item in property.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException($"Property '{name}' must contain only strings.");
            }

            values.Add(item.GetString()!);
        }

        return values;
    }

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierRegex();
}
