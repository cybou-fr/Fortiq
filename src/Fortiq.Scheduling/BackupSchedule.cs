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
    bool Enabled = true);

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
        ArgumentNullException.ThrowIfNull(state);

        if (!schedule.Enabled)
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
        var next = schedule.Recurrence.NextOccurrence(reference);

        if (next > now)
        {
            return new DueDecision(DueVerdict.NotYet, null, next);
        }

        if (schedule.CatchUp == CatchUp.Skip)
        {
            // Everything up to now is forgone; the schedule waits for the next occurrence.
            return new DueDecision(DueVerdict.NotYet, null, schedule.Recurrence.NextOccurrence(now));
        }

        // One occurrence is owed however many were missed: the source is only in one state now, so
        // running the same backup several times would record the same present repeatedly.
        return new DueDecision(DueVerdict.Due, next, schedule.Recurrence.NextOccurrence(now));
    }
}
