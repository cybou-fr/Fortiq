using Fortiq.Application;
using Fortiq.Domain;
using Fortiq.Scheduling;

namespace Fortiq.Scheduling.Tests;

/// <summary>
/// Scheduled retention. The only operation on this schedule that destroys anything, so most of what
/// is worth testing is what it refuses to do.
/// </summary>
public sealed class ScheduledRetentionTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);

    private static BackupSchedule Schedule(Recurrence? recurrence = null, RetentionPolicy? policy = null) => new(
        "documents",
        "repo",
        "kit",
        "source",
        "workstation:documents",
        new EveryInterval(TimeSpan.FromHours(6)),
        RetentionRecurrence: recurrence,
        Retention: policy);

    [Fact]
    public void ASilentScheduleKeepsEverything()
    {
        // The whole safety argument in one assertion. A file that says nothing about retention is
        // not asking for a sensible default; there is no safe default for deleting somebody's
        // backups, and silence can only mean keep.
        var decision = ScheduleDecision.EvaluateRetention(
            Schedule(),
            new ScheduleState("documents.retention"),
            Now);

        Assert.Equal(DueVerdict.Disabled, decision.Verdict);
    }

    [Fact]
    public void ARecurrenceWithoutAPolicyIsNotRetention()
    {
        // Would otherwise be a schedule for deleting snapshots according to no rule at all.
        var decision = ScheduleDecision.EvaluateRetention(
            Schedule(new EveryInterval(TimeSpan.FromDays(7))),
            new ScheduleState("documents.retention", LastSuccessAt: Now.AddDays(-30)),
            Now);

        Assert.Equal(DueVerdict.Disabled, decision.Verdict);
    }

    [Fact]
    public void APolicyThatKeepsNothingIsNotRetention()
    {
        var decision = ScheduleDecision.EvaluateRetention(
            Schedule(new EveryInterval(TimeSpan.FromDays(7)), new RetentionPolicy()),
            new ScheduleState("documents.retention", LastSuccessAt: Now.AddDays(-30)),
            Now);

        Assert.Equal(DueVerdict.Disabled, decision.Verdict);
    }

    [Fact]
    public void RetentionHistoryIsKeptApartFromBackupsAndDrills()
    {
        var schedule = Schedule();

        // Three different questions with three different answers. Sharing a key would make a
        // successful backup look like a completed retention run, or the reverse.
        Assert.Equal("documents.retention", schedule.RetentionStateId);
        Assert.NotEqual(schedule.RetentionStateId, schedule.DrillStateId);
        Assert.NotEqual(schedule.RetentionStateId, schedule.Id);
    }

    [Fact]
    public async Task ABusyRepositoryIsLeftAloneAndNotAskedAgainImmediately()
    {
        var schedule = Schedule(new EveryInterval(TimeSpan.FromDays(7)), new RetentionPolicy(KeepDaily: 7));
        var store = new MemoryStore(schedule);
        await store.WriteStateAsync(new ScheduleState("documents.retention", LastSuccessAt: Now.AddDays(-8)), default);

        var retention = new FailingRetention(new RepositoryBusyException("Something else is using this repository."));
        var runner = new ScheduledRetentionRunner(store, retention, new FixedClock(Now));

        var first = Assert.Single(await runner.RunDueAsync(CancellationToken.None));
        Assert.Contains("using this repository", first.Failure!, StringComparison.Ordinal);

        // Retention is the one operation where retrying eagerly is worse than waiting: everything it
        // does is irreversible, and the repository is busy precisely when a backup or a restore is
        // in flight.
        Assert.Equal(DueVerdict.NotYet, Assert.Single(await runner.RunDueAsync(CancellationToken.None)).Verdict);
        Assert.Equal(1, retention.Attempts);
    }

    [Fact]
    public async Task ASuccessfulRunRecordsWhatItRemoved()
    {
        var schedule = Schedule(new EveryInterval(TimeSpan.FromDays(7)), new RetentionPolicy(KeepDaily: 7));
        var store = new MemoryStore(schedule);
        await store.WriteStateAsync(new ScheduleState("documents.retention", LastSuccessAt: Now.AddDays(-8)), default);

        var runner = new ScheduledRetentionRunner(
            store,
            new SucceedingRetention(["a", "b"], pruned: false),
            new FixedClock(Now));

        var outcome = Assert.Single(await runner.RunDueAsync(CancellationToken.None));

        Assert.Equal(2, outcome.Removed);
        Assert.False(outcome.Pruned);
        Assert.Null(outcome.Failure);
        Assert.Equal(Now, (await store.ReadStateAsync("documents.retention", default)).LastSuccessAt);
    }

    [Fact]
    public async Task AMachineThatWasOffForAMonthForgetsOnce()
    {
        var schedule = Schedule(new EveryInterval(TimeSpan.FromDays(7)), new RetentionPolicy(KeepDaily: 7));
        var store = new MemoryStore(schedule);
        await store.WriteStateAsync(new ScheduleState("documents.retention", LastSuccessAt: Now.AddDays(-30)), default);

        var retention = new SucceedingRetention([], pruned: false);
        var runner = new ScheduledRetentionRunner(store, retention, new FixedClock(Now));

        await runner.RunDueAsync(CancellationToken.None);

        // Retention is idempotent in effect but not in cost, and four catch-up prunes in a row on a
        // machine that just came back is the last thing anybody wants.
        Assert.Equal(1, retention.Attempts);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class MemoryStore(params BackupSchedule[] schedules) : IScheduleStore
    {
        private readonly Dictionary<string, ScheduleState> _states = [];

        public Task<IReadOnlyList<BackupSchedule>> ReadSchedulesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<BackupSchedule>>(schedules);

        public Task<ScheduleState> ReadStateAsync(string scheduleId, CancellationToken cancellationToken) =>
            Task.FromResult(_states.TryGetValue(scheduleId, out var state) ? state : new ScheduleState(scheduleId));

        public Task WriteStateAsync(ScheduleState state, CancellationToken cancellationToken)
        {
            _states[state.ScheduleId] = state;
            return Task.CompletedTask;
        }
    }

    private sealed class SucceedingRetention(IReadOnlyList<string> removed, bool pruned) : IScheduledRetention
    {
        public int Attempts { get; private set; }

        public Task<RetentionReceipt> RunAsync(BackupSchedule schedule, CancellationToken cancellationToken)
        {
            Attempts++;
            return Task.FromResult(new RetentionReceipt(
                Guid.NewGuid(),
                RepositoryId.Create(),
                ["kept"],
                removed,
                pruned));
        }
    }

    private sealed class FailingRetention(Exception failure) : IScheduledRetention
    {
        public int Attempts { get; private set; }

        public Task<RetentionReceipt> RunAsync(BackupSchedule schedule, CancellationToken cancellationToken)
        {
            Attempts++;
            return Task.FromException<RetentionReceipt>(failure);
        }
    }
}
