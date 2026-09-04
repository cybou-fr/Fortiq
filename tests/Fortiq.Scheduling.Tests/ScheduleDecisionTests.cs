using Fortiq.Scheduling;

namespace Fortiq.Scheduling.Tests;

/// <summary>
/// Whether a schedule is due, given what it has already done. The interesting case is a machine that
/// was off: it owes a backup, not a queue of them.
/// </summary>
public sealed class ScheduleDecisionTests
{
    private static readonly DateTimeOffset Noon = new(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AScheduleThatRanRecentlyIsNotDueYet()
    {
        var schedule = Every(TimeSpan.FromHours(6));
        var state = new ScheduleState(schedule.Id, LastSuccessAt: Noon.AddHours(-1));

        var decision = ScheduleDecision.Evaluate(schedule, state, Noon);

        Assert.Equal(DueVerdict.NotYet, decision.Verdict);
        Assert.Equal(Noon.AddHours(5), decision.NextOccurrence);
    }

    [Fact]
    public void AScheduleWhoseIntervalHasElapsedIsDue()
    {
        var schedule = Every(TimeSpan.FromHours(6));
        var state = new ScheduleState(schedule.Id, LastSuccessAt: Noon.AddHours(-7));

        var decision = ScheduleDecision.Evaluate(schedule, state, Noon);

        Assert.Equal(DueVerdict.Due, decision.Verdict);
        Assert.Equal(Noon.AddHours(-1), decision.DueSince);
    }

    [Fact]
    public void AWeekOfMissedOccurrencesOwesOneBackupRatherThanAWeekOfThem()
    {
        var schedule = Every(TimeSpan.FromHours(6));
        var state = new ScheduleState(schedule.Id, LastSuccessAt: Noon.AddDays(-7));

        var decision = ScheduleDecision.Evaluate(schedule, state, Noon);

        // The source is only in one state now, so running the missed occurrences would record the
        // same present over and over.
        Assert.Equal(DueVerdict.Due, decision.Verdict);
        Assert.Equal(Noon.AddDays(-7).AddHours(6), decision.DueSince);
        Assert.Equal(Noon.AddHours(6), decision.NextOccurrence);
    }

    [Fact]
    public void AScheduleThatSkipsWhatItMissedWaitsForTheNextOccurrence()
    {
        var schedule = Every(TimeSpan.FromHours(6)) with { CatchUp = CatchUp.Skip };
        var state = new ScheduleState(schedule.Id, LastSuccessAt: Noon.AddDays(-7));

        var decision = ScheduleDecision.Evaluate(schedule, state, Noon);

        Assert.Equal(DueVerdict.NotYet, decision.Verdict);
        Assert.Equal(Noon.AddHours(6), decision.NextOccurrence);
    }

    [Fact]
    public void AScheduleThatHasNeverRunWaitsForItsFirstOccurrence()
    {
        var schedule = Every(TimeSpan.FromHours(6));

        var decision = ScheduleDecision.Evaluate(schedule, new ScheduleState(schedule.Id), Noon);

        // Adding a schedule does not itself mean a backup is overdue.
        Assert.Equal(DueVerdict.NotYet, decision.Verdict);
        Assert.Equal(Noon.AddHours(6), decision.NextOccurrence);
    }

    [Fact]
    public void AFailedAttemptStillMovesTheSchedule()
    {
        var schedule = Every(TimeSpan.FromHours(6));
        var state = new ScheduleState(schedule.Id, LastAttemptAt: Noon.AddMinutes(-5), LastFailure: "the repository was busy");

        // Otherwise a schedule whose backup keeps failing would be due continuously and retry in a
        // tight loop.
        var decision = ScheduleDecision.Evaluate(schedule, state, Noon);

        Assert.Equal(DueVerdict.NotYet, decision.Verdict);
    }

    [Fact]
    public void ADisabledScheduleIsNeverDue()
    {
        var schedule = Every(TimeSpan.FromHours(6)) with { Enabled = false };
        var state = new ScheduleState(schedule.Id, LastSuccessAt: Noon.AddYears(-1));

        Assert.Equal(DueVerdict.Disabled, ScheduleDecision.Evaluate(schedule, state, Noon).Verdict);
    }

    private static BackupSchedule Every(TimeSpan period) => new(
        "documents",
        "C:/repository",
        "C:/kit",
        "C:/source",
        "workstation:documents",
        new EveryInterval(period));
}
