using System.Text.Json;
using System.Text.Json.Nodes;
using Fortiq.CommunityModel;

namespace Fortiq.Assistant;

/// <summary>
/// The shape a model may propose a backup task in, and how that becomes a validated draft.
/// </summary>
/// <remarks>
/// Small, flat, and nothing like the domain model. A two-billion-parameter model asked to emit
/// nested resources with cross-referencing identifiers spends its budget satisfying the shape and
/// produces something well-formed and empty; asked for a folder, a schedule and a destination it
/// produces what somebody actually said. The identifiers, the route, the encryption profile and
/// every reference between them are composed here, by code, from the catalogue.
///
/// That division is not only about model size. Identifiers decide which existing resource a proposal
/// touches, and a model choosing them is a model deciding, from a sentence, which repository gets a
/// new retention policy. It names a folder and a destination; the code resolves what those are.
/// </remarks>
public static class TaskProposalSchema
{
    public static JsonNode Schema { get; } = new JsonObject
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            // Every description here was earned. Without them the pinned model copied the folder out
            // of the background context instead of the request, and put a path in the name.
            ["name"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "A short name for the task. Not a path."
            },
            ["sourcePath"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "The folder the person asked to back up, copied from their request."
            },
            ["storage"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "The name of a storage that already exists on this PC."
            },
            ["schedule"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["kind"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["enum"] = new JsonArray("manual", "daily", "everyHours")
                    },
                    ["timeOfDay"] = new JsonObject { ["type"] = "string", ["description"] = "HH:mm, only when kind is daily." },
                    ["hours"] = new JsonObject { ["type"] = "integer", ["description"] = "Only when kind is everyHours." }
                },
                ["required"] = new JsonArray("kind"),
                ["additionalProperties"] = false
            },
            ["keepDaily"] = new JsonObject { ["type"] = "integer", ["description"] = "Only if the person asked for it." },
            ["keepMonthly"] = new JsonObject { ["type"] = "integer", ["description"] = "Only if the person asked for it." }
        },
        ["required"] = new JsonArray("name", "sourcePath", "storage", "schedule"),
        ["additionalProperties"] = false
    };

    public const string Instruction =
        "Propose one backup task. Name the folder exactly as the person named it, and choose a "
        + "storage from the ones listed as existing on this PC - never invent one. If you do not "
        + "know which folder or which storage they mean, do not guess.";

    /// <summary>
    /// Turns what the model said into a proposal against this machine's resources.
    /// </summary>
    /// <remarks>
    /// Resolution is deliberately forgiving about names and unforgiving about existence. A person
    /// says "the NAS" and a model repeats it, so a storage is matched by name as well as by
    /// identifier - but nothing is created to satisfy a reference. A storage that does not exist
    /// stays unresolved and the draft fails validation saying so, which is a sentence somebody can
    /// act on. Inventing one would produce a task that looks configured and fails at 2am.
    ///
    /// The encryption profile is taken from a route that already writes to that storage, never
    /// composed. Who can decrypt a backup is not a thing to infer from a sentence; where no existing
    /// route answers it, the proposal carries no profile and validation refuses it.
    /// </remarks>
    public static TaskProposal? Compose(string json, ResourceCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }

        var name = Text(root, "name");
        var sourcePath = Text(root, "sourcePath");
        var storageName = Text(root, "storage");
        if (name is null || sourcePath is null || storageName is null)
        {
            return null;
        }

        var slug = Slug(name);
        var storage = Resolve(catalog, storageName);

        // An existing source for this folder, or a new one carried by the proposal. Matching on the
        // path is what stops "back up Documents again, to the NAS" creating a second Documents.
        var existingSource = catalog.Sources.FirstOrDefault(
            source => string.Equals(source.Path, sourcePath, StringComparison.OrdinalIgnoreCase));

        var sourceId = existingSource?.Id ?? $"source-{slug}";
        var route = new BackupRoute(
            $"route-{slug}",
            storage?.Id ?? storageName,
            catalog.Engines.Count > 0 ? catalog.Engines[0].Id : "engine-restic",
            ProfileFor(catalog, storage) ?? string.Empty,
            Retention(root),
            RetentionTrigger: Retention(root) is null ? null : new IntervalTrigger(TimeSpan.FromDays(1)));

        return new TaskProposal(
            new BackupTask($"task-{slug}", name, [sourceId], ScheduleOf(root), [route.Id]),
            [route],
            NewSources: existingSource is null
                ? [new Source(sourceId, System.IO.Path.GetFileName(sourcePath.TrimEnd('\\', '/')), SourceKind.Folder, sourcePath)]
                : null);
    }

    private static Storage? Resolve(ResourceCatalog catalog, string named) =>
        catalog.Storages.FirstOrDefault(storage =>
            string.Equals(storage.Id, named, StringComparison.OrdinalIgnoreCase)
            || string.Equals(storage.Name, named, StringComparison.OrdinalIgnoreCase));

    private static string? ProfileFor(ResourceCatalog catalog, Storage? storage) =>
        storage is null
            ? null
            : catalog.Routes
                .FirstOrDefault(route => string.Equals(route.StorageId, storage.Id, StringComparison.Ordinal))
                ?.EncryptionProfileId;

    private static Trigger ScheduleOf(JsonNode? root)
    {
        var schedule = root?["schedule"];
        var kind = Text(schedule, "kind");

        if (string.Equals(kind, "everyHours", StringComparison.OrdinalIgnoreCase))
        {
            var hours = schedule?["hours"]?.GetValue<int>() ?? 0;
            return new IntervalTrigger(TimeSpan.FromHours(hours));
        }

        if (string.Equals(kind, "daily", StringComparison.OrdinalIgnoreCase))
        {
            // The machine's own zone, and by name. A model that guessed a zone would be guessing
            // which hour of somebody's night their disk spins up.
            return TimeOnly.TryParse(Text(schedule, "timeOfDay"), out var time)
                ? new DailyTrigger(time, TimeZoneInfo.Local.Id)
                : new DailyTrigger(new TimeOnly(2, 0), TimeZoneInfo.Local.Id);
        }

        return new ManualTrigger();
    }

    private static RetentionRule? Retention(JsonNode? root)
    {
        var daily = root?["keepDaily"]?.GetValue<int>();
        var monthly = root?["keepMonthly"]?.GetValue<int>();
        var rule = new RetentionRule(KeepDaily: daily, KeepMonthly: monthly);
        return rule.KeepsSomething ? rule : null;
    }

    private static string? Text(JsonNode? node, string property)
    {
        var value = node?[property]?.GetValue<string>();
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string Slug(string value)
    {
        var readable = new string(value.ToLowerInvariant()
            .Select(character => char.IsAsciiLetterOrDigit(character) ? character : '-')
            .ToArray())
            .Trim('-');

        while (readable.Contains("--", StringComparison.Ordinal))
        {
            readable = readable.Replace("--", "-", StringComparison.Ordinal);
        }

        return readable.Length == 0 ? "proposed" : readable;
    }
}
