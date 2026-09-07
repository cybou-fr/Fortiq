using System.Text;

namespace Fortiq.CommunityModel;

/// <summary>One invariant, stated in the form the model is given it.</summary>
public sealed record ProductRule(string Code, string Statement);

/// <summary>
/// The rules the assistant is told, every time, whatever it is asked.
/// </summary>
/// <remarks>
/// These are not there to make the model behave - a model cannot be relied upon to obey anything,
/// and every one of these is separately enforced by code that runs whether it obeyed or not. They
/// are there so that its answers are consistent with what will actually happen. An assistant that
/// says "I have set that up for you" when the code will produce a draft awaiting review is wrong in
/// a way that costs somebody their data, even though the code did the right thing.
/// </remarks>
public static class ProductRules
{
    public static ProductRule TaskDraftsOnly { get; } = new(
        "RULE-TASK-001",
        "Anything you propose is a draft. You never change what Fortiq is doing.");

    public static ProductRule HumanActivation { get; } = new(
        "RULE-TASK-002",
        "Only a person can activate a task, by reading a draft and accepting it.");

    public static ProductRule SeparateAuthorities { get; } = new(
        "RULE-KEY-001",
        "Reaching the backup bytes and decrypting them are separate authorities. A storage login is "
        + "not a recovery key and never substitutes for one.");

    public static ProductRule WriterRequired { get; } = new(
        "RULE-RESTIC-001",
        "A task that runs on a schedule needs a writer key, or it cannot unlock itself to write.");

    public static ProductRule EvidenceDecidesRecovery { get; } = new(
        "RULE-RECOVERY-001",
        "Whether data is recoverable is decided by evidence Fortiq recorded, never by you. Do not "
        + "tell anybody their backups are safe; point them at what was actually checked.");

    public static ProductRule NoSecrets { get; } = new(
        "RULE-SECRET-001",
        "You are never given recovery phrases, passwords or storage keys, and cannot retrieve them. "
        + "If asked for one, say that Fortiq does not show them to you.");

    public static IReadOnlyList<ProductRule> All { get; } =
        [TaskDraftsOnly, HumanActivation, SeparateAuthorities, WriterRequired, EvidenceDecidesRecovery, NoSecrets];
}

/// <summary>
/// One thing that happened, or is currently true, on this machine.
/// </summary>
/// <param name="Code">Which kind of fact, so a screen or a test can key on it.</param>
/// <param name="Subject">What it is about, by name.</param>
/// <param name="Detail">The fact, in words.</param>
/// <remarks>
/// Facts are supplied to the builder rather than gathered by it. The health model and the receipt
/// ledger already decide what is true about a repository, and a context builder that recomputed any
/// of that would become a second opinion on recoverability - which is the one thing RULE-RECOVERY-001
/// exists to prevent.
/// </remarks>
public sealed record OperationalFact(string Code, string Subject, string Detail);

/// <summary>
/// Everything the assistant is told before it is asked anything.
/// </summary>
/// <remarks>
/// A small local model cannot infer Fortiq's capabilities, invariants and configuration from a
/// manual, and should not be asked to. It is given a compact, versioned, deterministic summary and
/// reasons over that.
///
/// The whole envelope is data, not instruction - including the parts Fortiq wrote. Folder names,
/// storage names and task names in here came off somebody's disk, and a folder called "ignore
/// previous instructions" is a folder anybody can create. <see cref="Render"/> therefore produces
/// text meant to go inside the evidence fence, and the product rules are stated as rules about the
/// assistant rather than as instructions that a resource name could plausibly imitate.
/// </remarks>
public sealed record AssistantContext(
    IReadOnlyList<ProductRule> ProductRules,
    CommunityCapabilities Capabilities,
    ResourceCatalog Resources,
    IReadOnlyList<OperationalFact> OperationalFacts,
    IReadOnlyList<string> DraftSummaries)
{
    public const string Schema = "fortiq.assistant-context";

    public const int Version = 1;

    /// <summary>
    /// The context as the text the model is given.
    /// </summary>
    /// <remarks>
    /// Compact on purpose. The budget is a small model's eight to sixteen thousand tokens, most of
    /// which belongs to the conversation, so this says what is true in as few words as it can and
    /// nothing that could be worked out from something else it says.
    /// </remarks>
    public string Render()
    {
        var text = new StringBuilder();

        text.Append("FORTIQ CONTEXT v").Append(Version).Append('\n').Append('\n');

        text.Append("RULES\n");
        foreach (var rule in ProductRules)
        {
            text.Append(rule.Code).Append(": ").Append(rule.Statement).Append('\n');
        }

        text.Append("\nTHIS BUILD CAN\n");
        text.Append("engine: ").Append(Capabilities.Engine.Name).Append(' ').Append(Capabilities.Engine.Version).Append('\n');
        text.Append("storage: ").Append(string.Join(", ", Capabilities.Backends)).Append('\n');
        text.Append("triggers: ").Append(string.Join(", ", Capabilities.Triggers)).Append('\n');
        text.Append("Anything not listed here does not work yet. Do not offer it.\n");

        text.Append("\nPROTECTED ON THIS PC\n");
        if (Resources.Tasks.Count == 0)
        {
            text.Append("Nothing is protected yet.\n");
        }

        foreach (var task in Resources.Tasks)
        {
            text.Append("task ").Append(task.Name).Append(": ").Append(Describe(task.Trigger));
            text.Append(task.Enabled ? string.Empty : ", paused");
            text.Append('\n');

            foreach (var source in Resources.SourcesOf(task))
            {
                text.Append("  from ").Append(source.Path).Append('\n');
            }

            foreach (var route in Resources.RoutesOf(task))
            {
                text.Append("  to ").Append(Describe(route)).Append('\n');
            }
        }

        if (DraftSummaries.Count > 0)
        {
            text.Append("\nDRAFTS AWAITING A PERSON\n");
            foreach (var draft in DraftSummaries)
            {
                text.Append(draft).Append('\n');
            }
        }

        if (OperationalFacts.Count > 0)
        {
            text.Append("\nWHAT FORTIQ RECORDED\n");
            foreach (var fact in OperationalFacts)
            {
                text.Append(fact.Subject).Append(": ").Append(fact.Detail).Append('\n');
            }
        }

        return text.ToString();
    }

    private string Describe(BackupRoute route)
    {
        var storage = Resources.Storage(route.StorageId);
        var profile = Resources.EncryptionProfile(route.EncryptionProfileId);
        var described = new StringBuilder(storage?.Name ?? route.StorageId);

        if (storage is not null)
        {
            // Whether a credential is configured, never which one and never its contents. Spec 29
            // requires secrets to appear as state, and the model has nowhere to hold one anyway -
            // this is what makes that structural rather than a promise.
            described.Append(" (").Append(storage.Backend);
            if (storage.Backend != StorageBackend.FileSystem)
            {
                described.Append(storage.CredentialRef is null ? ", no credential configured" : ", credential configured");
            }

            described.Append(')');
        }

        if (profile is not null)
        {
            described.Append(Resources.HasConfirmedRecipient(profile)
                ? ", recovery key confirmed"
                : ", recovery key never confirmed");

            described.Append(profile.Writers.Count > 0 ? ", can write unattended" : ", cannot write unattended");
        }

        if (route.Retention is { } retention && retention.KeepsSomething)
        {
            described.Append(", keeps ").Append(Describe(retention));
        }

        described.Append(route.DrillTrigger is null ? ", no recovery drill" : ", drill " + Describe(route.DrillTrigger));
        return described.ToString();
    }

    private static string Describe(RetentionRule retention)
    {
        var parts = new List<string>();
        if (retention.KeepLast is > 0) parts.Add($"last {retention.KeepLast}");
        if (retention.KeepDaily is > 0) parts.Add($"{retention.KeepDaily} daily");
        if (retention.KeepWeekly is > 0) parts.Add($"{retention.KeepWeekly} weekly");
        if (retention.KeepMonthly is > 0) parts.Add($"{retention.KeepMonthly} monthly");
        if (retention.KeepYearly is > 0) parts.Add($"{retention.KeepYearly} yearly");
        if (retention.KeepWithin is { } within) parts.Add($"everything for {within.TotalDays:N0} days");
        return string.Join(" and ", parts);
    }

    private static string Describe(Trigger trigger) => trigger switch
    {
        ManualTrigger => "only when asked",
        OnceTrigger once => $"once, at {once.At:yyyy-MM-dd HH:mm}",
        IntervalTrigger interval => $"every {Describe(interval.Period)}",
        DailyTrigger daily when daily.Days is { Count: > 0 } days =>
            $"{string.Join(" and ", days)} at {daily.TimeOfDay:HH\\:mm} {daily.TimeZoneId}",
        DailyTrigger daily => $"daily at {daily.TimeOfDay:HH\\:mm} {daily.TimeZoneId}",
        FileChangeTrigger => "when files change",
        _ => "on an unrecognised schedule"
    };

    private static string Describe(TimeSpan period) =>
        period.TotalHours >= 24 && period.TotalHours % 24 == 0
            ? $"{period.TotalDays:N0} day{(period.TotalDays == 1 ? string.Empty : "s")}"
            : $"{period.TotalHours:N0} hour{(period.TotalHours == 1 ? string.Empty : "s")}";
}

/// <summary>
/// Assembles the context, deterministically and within a budget.
/// </summary>
/// <remarks>
/// It selects; it does not compute. Every fact it carries was decided somewhere that owns that
/// question - the catalogue for configuration, the health model for recoverability, the receipt
/// ledger for what happened - and the builder's only job is to choose which of them are relevant and
/// leave the rest out. That boundary is what keeps the assistant from becoming a second opinion on
/// whether somebody's data can be recovered.
/// </remarks>
public sealed class AssistantContextBuilder(CommunityCapabilities? capabilities = null)
{
    /// <summary>
    /// How many recorded facts to carry.
    /// </summary>
    /// <remarks>
    /// A machine with years of history has thousands, and a small model's context is mostly needed
    /// for the conversation. Spec 29 asks for bounded relevance rather than a dump of everything,
    /// and an unbounded builder would silently push the question out of the window on exactly the
    /// installations with the most to say.
    /// </remarks>
    public const int DefaultFactLimit = 24;

    private readonly CommunityCapabilities _capabilities = capabilities ?? CommunityCapabilities.Current;

    public AssistantContext Build(
        ResourceCatalog catalog,
        IReadOnlyList<OperationalFact>? facts = null,
        IReadOnlyList<Draft<TaskProposal>>? drafts = null,
        int factLimit = DefaultFactLimit)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(factLimit);

        return new AssistantContext(
            Fortiq.CommunityModel.ProductRules.All,
            _capabilities,
            catalog,
            (facts ?? []).Take(factLimit).ToList(),
            (drafts ?? []).Where(draft => draft.State is DraftState.ReadyForReview or DraftState.Invalid)
                .Select(Summarise)
                .ToList());
    }

    /// <summary>
    /// A draft in one line, saying who wrote it and whether it is usable.
    /// </summary>
    /// <remarks>
    /// Its own earlier proposals are the thing an assistant is most likely to misremember, and the
    /// most damaging to misremember: "I set that up" about something still awaiting review is how
    /// somebody stops checking.
    /// </remarks>
    private static string Summarise(Draft<TaskProposal> draft)
    {
        var origin = draft.Origin == DraftOrigin.Assistant ? "you proposed" : "someone drafted";
        var state = draft.State == DraftState.Invalid
            ? $"cannot be used: {string.Join("; ", draft.Blocking.Select(finding => finding.Detail))}"
            : "waiting for a person to accept it";

        return $"{origin} '{draft.Proposed.Task.Name}' - {state}";
    }
}
