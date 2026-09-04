using Fortiq.Scheduling;

namespace Fortiq.Scheduling.Tests;

/// <summary>
/// Scheduled restore drills. A drill is the only thing that turns "backed up" into "known to come
/// back" without a person, so what it costs and when it runs are decisions worth pinning down.
/// </summary>
public sealed class ScheduledDrillTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);

    private static BackupSchedule Schedule(Recurrence? drill) => new(
        "documents",
        @"C:\repo",
        @"C:\kit",
        @"C:\source",
        "workstation:documents",
        new EveryInterval(TimeSpan.FromHours(6)),
        DrillRecurrence: drill);

    [Fact]
    public void ARepositoryWithNoDrillRecurrenceIsNeverDrilled()
    {
        var decision = ScheduleDecision.EvaluateDrill(
            Schedule(drill: null),
            new ScheduleState("documents.drill"),
            Now);

        // Not an error and not a schedule that keeps almost running: a full restore of someone's
        // source is not something to start because a field was left out of a file.
        Assert.Equal(DueVerdict.Disabled, decision.Verdict);
    }

    [Fact]
    public void DrillStateIsKeptApartFromTheBackupState()
    {
        // If they shared a key, a failed drill would make the next backup look as though it had
        // already been attempted, and a successful backup would make the repository look proven.
        Assert.Equal("documents.drill", Schedule(null).DrillStateId);
        Assert.NotEqual(Schedule(null).Id, Schedule(null).DrillStateId);
    }

    [Fact]
    public void ADrillThatHasNeverRunIsNotDueImmediately()
    {
        var decision = ScheduleDecision.EvaluateDrill(
            Schedule(new EveryInterval(TimeSpan.FromDays(7))),
            new ScheduleState("documents.drill"),
            Now);

        // The first week after protecting a folder is when a person is most likely to be watching.
        // Restoring the whole source the moment the wizard closes would be a surprise, and there is
        // nothing to prove yet that the provisioner has not already proven.
        Assert.Equal(DueVerdict.NotYet, decision.Verdict);
    }

    [Fact]
    public void ADrillBecomesDueOnceItsPeriodHasPassed()
    {
        var decision = ScheduleDecision.EvaluateDrill(
            Schedule(new EveryInterval(TimeSpan.FromDays(7))),
            new ScheduleState("documents.drill", LastSuccessAt: Now.AddDays(-8)),
            Now);

        Assert.Equal(DueVerdict.Due, decision.Verdict);
    }

    [Fact]
    public void AMachineThatWasOffForAMonthOwesOneDrill()
    {
        var schedule = Schedule(new EveryInterval(TimeSpan.FromDays(7)));
        var state = new ScheduleState("documents.drill", LastSuccessAt: Now.AddDays(-30));

        var decision = ScheduleDecision.EvaluateDrill(schedule, state, Now);

        // Four missed occurrences, one restore owed. Each drill restores the entire source, and
        // running four in a row would prove the same thing four times at four times the cost.
        Assert.Equal(DueVerdict.Due, decision.Verdict);
        Assert.True(decision.NextOccurrence > Now);
    }

    [Fact]
    public async Task AFailedDrillIsRecordedAndDoesNotRetryOnTheNextPass()
    {
        var store = new MemoryStore(Schedule(new EveryInterval(TimeSpan.FromDays(7))));
        await store.WriteStateAsync(new ScheduleState("documents.drill", LastSuccessAt: Now.AddDays(-8)), default);

        var drill = new FailingDrill("The repository could not be opened.");
        var runner = new ScheduledDrillRunner(store, drill, new FixedClock(Now));

        var first = Assert.Single(await runner.RunDueAsync(CancellationToken.None));
        Assert.Equal("The repository could not be opened.", first.Failure);

        var state = await store.ReadStateAsync("documents.drill", default);
        Assert.Equal(Now, state.LastAttemptAt);
        // The reason survives, and the last success is not erased: a repository that could be
        // restored a week ago and cannot be today has a different history from one never proven.
        Assert.Equal("The repository could not be opened.", state.LastFailure);
        Assert.Equal(Now.AddDays(-8), state.LastSuccessAt);

        // A second pass at the same moment must not restore the whole source again against a
        // repository that has just shown it cannot be read.
        Assert.Equal(DueVerdict.NotYet, Assert.Single(await runner.RunDueAsync(CancellationToken.None)).Verdict);
        Assert.Equal(1, drill.Attempts);
    }

    [Fact]
    public async Task ASuccessfulDrillRecordsWhatItRestored()
    {
        var store = new MemoryStore(Schedule(new EveryInterval(TimeSpan.FromDays(7))));
        await store.WriteStateAsync(new ScheduleState("documents.drill", LastSuccessAt: Now.AddDays(-8)), default);

        var runner = new ScheduledDrillRunner(
            store,
            new SucceedingDrill(new DrillResult("abc123", 6, 65626)),
            new FixedClock(Now));

        var outcome = Assert.Single(await runner.RunDueAsync(CancellationToken.None));

        Assert.Null(outcome.Failure);
        Assert.Equal("abc123", outcome.SnapshotId);

        var state = await store.ReadStateAsync("documents.drill", default);
        Assert.Equal(Now, state.LastSuccessAt);
        Assert.Equal("abc123", state.LastSnapshotId);
        Assert.Null(state.LastFailure);
    }

    [Fact]
    public async Task OneRepositoryThatCannotBeDrilledDoesNotStopTheRest()
    {
        var store = new MemoryStore(
            Schedule(new EveryInterval(TimeSpan.FromDays(7))),
            Schedule(new EveryInterval(TimeSpan.FromDays(7))) with { Id = "photos" });

        await store.WriteStateAsync(new ScheduleState("documents.drill", LastSuccessAt: Now.AddDays(-8)), default);
        await store.WriteStateAsync(new ScheduleState("photos.drill", LastSuccessAt: Now.AddDays(-8)), default);

        var runner = new ScheduledDrillRunner(
            store,
            new FailingFirstDrill("documents", "unreachable"),
            new FixedClock(Now));

        var outcomes = await runner.RunDueAsync(CancellationToken.None);

        Assert.Equal("unreachable", outcomes.Single(outcome => outcome.ScheduleId == "documents").Failure);
        Assert.Null(outcomes.Single(outcome => outcome.ScheduleId == "photos").Failure);
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

    private sealed class SucceedingDrill(DrillResult result) : IScheduledDrill
    {
        public Task<DrillResult> RunAsync(BackupSchedule schedule, CancellationToken cancellationToken) =>
            Task.FromResult(result);
    }

    private sealed class FailingDrill(string message) : IScheduledDrill
    {
        public int Attempts { get; private set; }

        public Task<DrillResult> RunAsync(BackupSchedule schedule, CancellationToken cancellationToken)
        {
            Attempts++;
            return Task.FromException<DrillResult>(new InvalidOperationException(message));
        }
    }

    private sealed class FailingFirstDrill(string failingId, string message) : IScheduledDrill
    {
        public Task<DrillResult> RunAsync(BackupSchedule schedule, CancellationToken cancellationToken) =>
            schedule.Id == failingId
                ? Task.FromException<DrillResult>(new InvalidOperationException(message))
                : Task.FromResult(new DrillResult("snapshot", 1, 1));
    }
}

/// <summary>
/// One damaged file must never be able to stop the whole machine. Schedule files were isolated
/// already; their recorded history was not, and a truncated state file could end a pass before the
/// schedules after it were looked at.
/// </summary>
public sealed class CorruptStateIsolationTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("fortiq-corrupt-state-").FullName;

    [Fact]
    public async Task ARepositoryWithUnreadableHistoryDoesNotStopTheOthers()
    {
        await WriteScheduleAsync("aaa-broken");
        await WriteScheduleAsync("bbb-healthy");

        var state = Path.Combine(_directory, "state");
        Directory.CreateDirectory(state);

        // A power cut during a write leaves exactly this: a file that exists and is not JSON.
        await File.WriteAllTextAsync(Path.Combine(state, "aaa-broken.json"), "{\"lastAttemptAt\":");

        // The healthy schedule has run before, which is what makes it due now: a schedule that has
        // never run is measured from the present and is not yet owed anything.
        await new FileSystemScheduleStore(_directory).WriteStateAsync(
            new ScheduleState("bbb-healthy", LastSuccessAt: DateTimeOffset.UtcNow.AddHours(-1)),
            CancellationToken.None);

        var store = new FileSystemScheduleStore(_directory);
        var backups = new RecordingBackup();
        var outcomes = await new ScheduledBackupRunner(store, backups).RunDueAsync(CancellationToken.None);

        var broken = outcomes.Single(outcome => outcome.ScheduleId == "aaa-broken");
        Assert.Contains("could not be read", broken.Failure!, StringComparison.Ordinal);

        // The healthy schedule was still reached, and ran. Ordering matters here: the damaged one
        // sorts first, so a pass that gave up would never have got to this one.
        Assert.Null(outcomes.Single(outcome => outcome.ScheduleId == "bbb-healthy").Failure);
        Assert.Equal(["bbb-healthy"], backups.Ran);
    }

    [Fact]
    public async Task ARepositoryWithUnreadableDrillHistoryDoesNotStopTheOthers()
    {
        await WriteScheduleAsync("aaa-broken", drill: true);
        await WriteScheduleAsync("bbb-healthy", drill: true);

        var state = Path.Combine(_directory, "state");
        Directory.CreateDirectory(state);
        await File.WriteAllTextAsync(Path.Combine(state, "aaa-broken.drill.json"), "not json at all");

        var outcomes = await new ScheduledDrillRunner(
            new FileSystemScheduleStore(_directory),
            new RecordingDrill()).RunDueAsync(CancellationToken.None);

        Assert.Contains(
            "could not be read",
            outcomes.Single(outcome => outcome.ScheduleId == "aaa-broken").Failure!,
            StringComparison.Ordinal);

        Assert.Null(outcomes.Single(outcome => outcome.ScheduleId == "bbb-healthy").Failure);
    }

    private async Task WriteScheduleAsync(string id, bool drill = false)
    {
        var directory = Path.Combine(_directory, "schedules");
        Directory.CreateDirectory(directory);

        var extra = drill
            ? ",\n  \"drillRecurrence\": { \"kind\": \"interval\", \"period\": \"7.00:00:00\" }"
            : string.Empty;

        await File.WriteAllTextAsync(
            Path.Combine(directory, id + ".json"),
            $$"""
            {
              "schema": "fortiq.backup-schedule",
              "version": 1,
              "id": "{{id}}",
              "repository": "repo/{{id}}",
              "kit": "kit/{{id}}",
              "source": "source/{{id}}",
              "sourceStableId": "workstation:{{id}}",
              "recurrence": { "kind": "interval", "period": "00:00:01" }{{extra}}
            }
            """);
    }

    private sealed class RecordingBackup : Fortiq.Scheduling.IScheduledBackup
    {
        public List<string> Ran { get; } = [];

        public Task<Fortiq.Domain.BackupReceipt> RunAsync(BackupSchedule schedule, CancellationToken cancellationToken)
        {
            Ran.Add(schedule.Id);
            return Task.FromResult(new Fortiq.Domain.BackupReceipt(
                Guid.NewGuid(),
                Fortiq.Domain.RepositoryId.Create(),
                "snapshot"));
        }
    }

    private sealed class RecordingDrill : IScheduledDrill
    {
        public Task<DrillResult> RunAsync(BackupSchedule schedule, CancellationToken cancellationToken) =>
            Task.FromResult(new DrillResult("snapshot", 1, 1));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
