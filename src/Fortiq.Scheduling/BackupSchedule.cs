using Fortiq.Application;

namespace Fortiq.Scheduling;

/// <summary>What to do about occurrences that were missed while Fortiq was not running.</summary>
public enum CatchUp
{
    /// <summary>
    /// Run once, as soon as possible. A machine that was off for a week owes one backup, not seven:
    /// the older ones would back up the same present-day source over and over.
    /// </summary>
    Once,

    /// <summary>Skip what was missed and wait for the next occurrence.</summary>
    Skip
}

/// <summary>One thing Fortiq backs up, and when.</summary>
public sealed record BackupSchedule(
    string Id,
    string RepositoryLocation,
    string KitDirectory,
    string SourcePath,
    string SourceStableId,
    Recurrence Recurrence,
    SourceConsistency Consistency = SourceConsistency.Live,
    CatchUp CatchUp = CatchUp.Once,
    bool Enabled = true,
    Recurrence? DrillRecurrence = null,
    Recurrence? RetentionRecurrence = null,
    RetentionPolicy? Retention = null,
    PruneMode Prune = PruneMode.ForgetOnly)
{
    /// <summary>
    /// The state key under which this schedule's retention runs are tracked. Apart from the backup's
    /// and the drill's for the same reason: three different questions, three different histories.
    /// </summary>
    public string RetentionStateId => Id + ".retention";

    /// <summary>
    /// Whether retention is configured at all. Both halves are required: a recurrence without a
    /// policy would be a schedule for deleting snapshots according to no rule.
    /// </summary>
    public bool RetentionConfigured => RetentionRecurrence is not null && Retention is { } policy && policy.KeepsSomething;

    /// <summary>
    /// The state key under which this schedule's restore drills are tracked, kept apart from the
    /// backup's own state: a drill that failed must not make the next backup look overdue, and a
    /// backup that succeeded must not make a repository look recently proven.
    /// </summary>
    public string DrillStateId => Id + ".drill";
}

/// <summary>What has happened to a schedule so far. Kept apart from the schedule itself: one is
/// configuration a person edits, the other is history Fortiq writes.</summary>
public sealed record ScheduleState(
    string ScheduleId,
    DateTimeOffset? LastAttemptAt = null,
    DateTimeOffset? LastSuccessAt = null,
    string? LastSnapshotId = null,
    string? LastFailure = null);

/// <summary>Why a schedule did or did not run at a given moment.</summary>
public enum DueVerdict
{
    /// <summary>Due now, either on time or as the single catch-up for missed occurrences.</summary>
    Due,

    /// <summary>Not yet due.</summary>
    NotYet,

    /// <summary>Disabled, so it is never due.</summary>
    Disabled
}

public sealed record DueDecision(DueVerdict Verdict, DateTimeOffset? DueSince, DateTimeOffset NextOccurrence);

/// <summary>
/// Decides whether a schedule is due, given what it has already done. Pure: it reads a clock value
/// passed to it and touches nothing, which is what makes the awkward cases - a machine that was off
/// for a week, a clock that jumped - testable rather than hopeful.
/// </summary>
public static class ScheduleDecision
{
    public static DueDecision Evaluate(BackupSchedule schedule, ScheduleState state, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        return Evaluate(schedule.Recurrence, schedule.CatchUp, schedule.Enabled, state, now);
    }

    /// <summary>
    /// Whether a repository's restore drill is due. A schedule with no drill recurrence is not
    /// disabled, it simply has no drills; the verdict is the same and the reason is worth knowing.
    /// </summary>
    /// <remarks>
    /// Drills never catch up. A machine that was off for a month owes one proof of recovery, not
    /// four, and each one is a full restore of the source.
    /// </remarks>
    public static DueDecision EvaluateDrill(BackupSchedule schedule, ScheduleState state, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(schedule);

        return schedule.DrillRecurrence is null
            ? new DueDecision(DueVerdict.Disabled, null, DateTimeOffset.MaxValue)
            : Evaluate(schedule.DrillRecurrence, CatchUp.Once, schedule.Enabled, state, now);
    }

    /// <summary>
    /// Whether a repository's retention run is due. A schedule that does not configure retention is
    /// never due, which is not the same as disabled by a person and is reported the same way.
    /// </summary>
    /// <remarks>
    /// Retention deletes backups. Nothing here infers a policy, supplies a default, or treats a
    /// missing field as permission: a schedule that says nothing about retention keeps everything
    /// forever, which is the only safe reading of silence.
    /// </remarks>
    public static DueDecision EvaluateRetention(BackupSchedule schedule, ScheduleState state, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(schedule);

        return schedule.RetentionConfigured
            ? Evaluate(schedule.RetentionRecurrence!, CatchUp.Once, schedule.Enabled, state, now)
            : new DueDecision(DueVerdict.Disabled, null, DateTimeOffset.MaxValue);
    }

    private static DueDecision Evaluate(
        Recurrence recurrence,
        CatchUp catchUp,
        bool enabled,
        ScheduleState state,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (!enabled)
        {
            return new DueDecision(DueVerdict.Disabled, null, DateTimeOffset.MaxValue);
        }

        // The reference is the last time this schedule was acted on at all, successful or not. Using
        // the last success instead would make a schedule whose backup keeps failing due again on
        // every pass, retrying in a tight loop against a repository that is already unhappy.
        var attempted = state.LastAttemptAt;
        var succeeded = state.LastSuccessAt;
        var reference = (attempted, succeeded) switch
        {
            (null, null) => now,
            (null, { } success) => success,
            ({ } attempt, null) => attempt,
            ({ } attempt, { } success) => attempt > success ? attempt : success
        };
        var next = recurrence.NextOccurrence(reference);

        if (next > now)
        {
            return new DueDecision(DueVerdict.NotYet, null, next);
        }

        if (catchUp == CatchUp.Skip)
        {
            // Everything up to now is forgone; the schedule waits for the next occurrence.
            return new DueDecision(DueVerdict.NotYet, null, recurrence.NextOccurrence(now));
        }

        // One occurrence is owed however many were missed: the source is only in one state now, so
        // running the same backup several times would record the same present repeatedly.
        return new DueDecision(DueVerdict.Due, next, recurrence.NextOccurrence(now));
    }
}
