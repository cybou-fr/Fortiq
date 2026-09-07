namespace Fortiq.CommunityModel;

/// <summary>
/// When a task occurrence is asked for.
/// </summary>
/// <remarks>
/// A closed hierarchy rather than one record with nullable fields for every shape, so that a trigger
/// which is a daily time cannot also carry a settle window nobody will read. Deliberately without a
/// <c>NextOccurrence</c>: this is a read model, and the arithmetic - daylight saving, missed
/// occurrences, catch-up - already exists and is tested where the scheduler lives. Two
/// implementations of that would be one too many.
/// </remarks>
public abstract record Trigger
{
    private protected Trigger()
    {
    }
}

/// <summary>Only when somebody asks.</summary>
public sealed record ManualTrigger : Trigger;

/// <summary>Once, at a stated moment.</summary>
public sealed record OnceTrigger(DateTimeOffset At) : Trigger;

/// <summary>Every so often, measured from the last run rather than from a wall clock.</summary>
public sealed record IntervalTrigger(TimeSpan Period) : Trigger;

/// <summary>
/// At a time of day in a stated zone, on the stated days.
/// </summary>
/// <param name="TimeOfDay">The wall-clock time.</param>
/// <param name="TimeZoneId">The zone it is a wall-clock time in. Never the machine's current zone by implication.</param>
/// <param name="Days">Which days, or null for every day.</param>
public sealed record DailyTrigger(TimeOnly TimeOfDay, string TimeZoneId, IReadOnlyList<DayOfWeek>? Days = null) : Trigger;

/// <summary>
/// When the source changes, with the events coalesced.
/// </summary>
/// <param name="Settle">How long changes must stop before a run is asked for.</param>
/// <param name="MinimumInterval">The floor between runs, however busy the folder is.</param>
public sealed record FileChangeTrigger(TimeSpan Settle, TimeSpan MinimumInterval) : Trigger;

/// <summary>How many snapshots a route keeps.</summary>
/// <remarks>
/// Defined here rather than borrowed from the operations layer so this model has no dependencies at
/// all. That is the point of the read model: it can be projected, inspected, drafted against and
/// eventually written, without dragging in the execution core it is meant to outlive.
/// </remarks>
public sealed record RetentionRule(
    int? KeepLast = null,
    int? KeepDaily = null,
    int? KeepWeekly = null,
    int? KeepMonthly = null,
    int? KeepYearly = null,
    TimeSpan? KeepWithin = null)
{
    /// <summary>A rule that keeps nothing is not retention, it is deletion.</summary>
    public bool KeepsSomething =>
        KeepLast > 0 || KeepDaily > 0 || KeepWeekly > 0 || KeepMonthly > 0 || KeepYearly > 0
        || KeepWithin > TimeSpan.Zero;
}

/// <summary>
/// One independent copy a task produces.
/// </summary>
/// <param name="Id">Stable identifier for this copy.</param>
/// <param name="StorageId">Where it goes.</param>
/// <param name="EngineId">What format it is written in.</param>
/// <param name="EncryptionProfileId">Who can get it back.</param>
/// <param name="Retention">How much history it keeps, if any.</param>
/// <param name="RetentionTrigger">When retention runs.</param>
/// <param name="DrillTrigger">When a recovery drill runs against this copy.</param>
/// <remarks>
/// Two routes on one task are two independently observable copies: one can be healthy while the
/// other has not been written for a month, and a model that aggregated them would let the healthy
/// one hide the other. Spec 25 is explicit that task aggregation must never conceal route-level
/// failure, and this is where that becomes structural rather than a rule somebody has to remember.
///
/// Retention and drill triggers are here although Spec 25 §4 lists only a retention policy on a
/// route. They exist today, per schedule, and dropping them would make this projection quietly
/// lossy - a read model that loses the drill schedule would let the GUI show a source as covered
/// while nothing was ever proving it can be restored. Where the specification and the running
/// system disagree about completeness, the running system wins and the specification is what gets
/// corrected.
/// </remarks>
public sealed record BackupRoute(
    string Id,
    string StorageId,
    string EngineId,
    string EncryptionProfileId,
    RetentionRule? Retention = null,
    Trigger? RetentionTrigger = null,
    Trigger? DrillTrigger = null);

/// <summary>What to do about occurrences missed while Fortiq was not running.</summary>
public enum CatchUpPolicy
{
    /// <summary>Run once, as soon as possible. A week off owes one backup, not seven.</summary>
    Once,

    /// <summary>Skip what was missed and wait for the next occurrence.</summary>
    Skip
}

/// <summary>
/// The primary declaration: which data, when, and into which independent copies.
/// </summary>
/// <param name="Id">Stable identifier.</param>
/// <param name="Name">What the person calls it.</param>
/// <param name="SourceIds">What it protects. More than one source may share a task.</param>
/// <param name="Trigger">When an occurrence is asked for.</param>
/// <param name="RouteIds">The copies it produces.</param>
/// <param name="Enabled">Whether it runs at all.</param>
/// <param name="CatchUp">What a missed occurrence means.</param>
/// <remarks>
/// A Task is not a Run, a Trigger is not a Task, a Route is not a Storage, and a Repository is not a
/// Task. Today all four are the same record, which is why "back up these two folders to these two
/// places" is not expressible: it is four schedules, four repositories, four recovery kits and four
/// recovery phrases, for one intention a person would describe in one sentence.
/// </remarks>
public sealed record BackupTask(
    string Id,
    string Name,
    IReadOnlyList<string> SourceIds,
    Trigger Trigger,
    IReadOnlyList<string> RouteIds,
    bool Enabled = true,
    CatchUpPolicy CatchUp = CatchUpPolicy.Once);
