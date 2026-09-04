using Fortiq.Application;
using Fortiq.Domain;
using Fortiq.Scheduling;

namespace Fortiq.Scheduling.Tests;

/// <summary>
/// Schedules as files someone edits, state as files Fortiq writes, and the pass that turns one into
/// the other.
/// </summary>
public sealed class ScheduleStoreAndRunnerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "fortiq-schedules-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task AScheduleFileIsReadAsWritten()
    {
        await WriteScheduleAsync("documents", """
            {
              "schema": "fortiq.backup-schedule",
              "version": 1,
              "id": "documents",
              "repository": "C:/repository",
              "kit": "C:/kit",
              "source": "C:/source",
              "sourceStableId": "workstation:documents",
              "consistency": "snapshot",
              "catchUp": "skip",
              "recurrence": { "kind": "dailyAt", "timeOfDay": "03:00", "timeZone": "W. Europe Standard Time", "days": ["Saturday"] }
            }
            """);

        var schedule = Assert.Single(await Store().ReadSchedulesAsync(CancellationToken.None));

        Assert.Equal("documents", schedule.Id);
        Assert.Equal(SourceConsistency.FileSystemSnapshot, schedule.Consistency);
        Assert.Equal(CatchUp.Skip, schedule.CatchUp);
        var recurrence = Assert.IsType<DailyAt>(schedule.Recurrence);
        Assert.Equal(new TimeOnly(3, 0), recurrence.TimeOfDay);
        Assert.Equal([DayOfWeek.Saturday], recurrence.Days);
    }

    [Fact]
    public async Task ARecurrenceThisBuildDoesNotKnowIsRefusedRatherThanApproximated()
    {
        await WriteScheduleAsync("odd", """
            {
              "schema": "fortiq.backup-schedule",
              "version": 1,
              "id": "odd",
              "repository": "C:/repository",
              "kit": "C:/kit",
              "source": "C:/source",
              "sourceStableId": "workstation:documents",
              "recurrence": { "kind": "everyFullMoon" }
            }
            """);

        // A schedule file decides what runs and when; guessing the nearest known kind would run
        // something nobody asked for.
        await Assert.ThrowsAsync<InvalidDataException>(() => Store().ReadSchedulesAsync(CancellationToken.None));
    }

    [Fact]
    public async Task StateRoundTripsAndStartsEmpty()
    {
        var store = Store();

        var initial = await store.ReadStateAsync("documents", CancellationToken.None);
        Assert.Null(initial.LastSuccessAt);

        var written = initial with
        {
            LastAttemptAt = new DateTimeOffset(2026, 9, 4, 3, 0, 0, TimeSpan.Zero),
            LastSuccessAt = new DateTimeOffset(2026, 9, 4, 3, 1, 0, TimeSpan.Zero),
            LastSnapshotId = new string('a', 64)
        };

        await store.WriteStateAsync(written, CancellationToken.None);
        var reloaded = await store.ReadStateAsync("documents", CancellationToken.None);

        Assert.Equal(written.LastAttemptAt, reloaded.LastAttemptAt);
        Assert.Equal(written.LastSuccessAt, reloaded.LastSuccessAt);
        Assert.Equal(written.LastSnapshotId, reloaded.LastSnapshotId);
    }

    [Fact]
    public async Task AScheduleIdThatWouldEscapeItsDirectoryIsRefused()
    {
        var store = Store();

        await Assert.ThrowsAsync<InvalidDataException>(
            () => store.WriteStateAsync(new ScheduleState("../../elsewhere"), CancellationToken.None));
    }

    [Fact]
    public async Task ADueScheduleRunsAndItsSuccessIsRecorded()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero));
        await WriteIntervalScheduleAsync("documents", TimeSpan.FromHours(6));
        var store = Store();
        await store.WriteStateAsync(
            new ScheduleState("documents", LastSuccessAt: clock.GetUtcNow().AddHours(-7)),
            CancellationToken.None);

        var backup = new RecordingBackup(new string('b', 64));
        var outcome = Assert.Single(await new ScheduledBackupRunner(store, backup, clock).RunDueAsync(CancellationToken.None));

        Assert.Equal(DueVerdict.Due, outcome.Verdict);
        Assert.Equal(new string('b', 64), outcome.SnapshotId);
        Assert.Equal("documents", Assert.Single(backup.Ran).Id);

        var state = await store.ReadStateAsync("documents", CancellationToken.None);
        Assert.Equal(clock.GetUtcNow(), state.LastSuccessAt);
        Assert.Null(state.LastFailure);
    }

    [Fact]
    public async Task AScheduleThatIsNotDueIsLeftAlone()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero));
        await WriteIntervalScheduleAsync("documents", TimeSpan.FromHours(6));
        var store = Store();
        await store.WriteStateAsync(
            new ScheduleState("documents", LastSuccessAt: clock.GetUtcNow().AddHours(-1)),
            CancellationToken.None);

        var backup = new RecordingBackup(new string('b', 64));
        var outcome = Assert.Single(await new ScheduledBackupRunner(store, backup, clock).RunDueAsync(CancellationToken.None));

        Assert.Equal(DueVerdict.NotYet, outcome.Verdict);
        Assert.Empty(backup.Ran);
    }

    [Fact]
    public async Task OneFailingScheduleDoesNotStopTheOthers()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero));
        await WriteIntervalScheduleAsync("a-broken", TimeSpan.FromHours(6));
        await WriteIntervalScheduleAsync("b-healthy", TimeSpan.FromHours(6));

        var store = Store();
        foreach (var id in new[] { "a-broken", "b-healthy" })
        {
            await store.WriteStateAsync(
                new ScheduleState(id, LastSuccessAt: clock.GetUtcNow().AddHours(-7)),
                CancellationToken.None);
        }

        var backup = new RecordingBackup(new string('c', 64), failFor: "a-broken");
        var outcomes = await new ScheduledBackupRunner(store, backup, clock).RunDueAsync(CancellationToken.None);

        Assert.Equal(2, outcomes.Count);
        Assert.Contains(outcomes, outcome => outcome.ScheduleId == "a-broken" && outcome.Failure is not null);
        Assert.Contains(outcomes, outcome => outcome.ScheduleId == "b-healthy" && outcome.SnapshotId is not null);

        // The failure is kept where the next pass will see it, and the attempt moves the schedule on
        // so it does not retry in a tight loop.
        var failed = await store.ReadStateAsync("a-broken", CancellationToken.None);
        Assert.Equal("the repository was unreachable", failed.LastFailure);
        Assert.Equal(clock.GetUtcNow(), failed.LastAttemptAt);

        // A failed attempt does not erase when this schedule last succeeded: that is the fact
        // someone asks for when deciding how stale a backup is.
        Assert.Equal(clock.GetUtcNow().AddHours(-7), failed.LastSuccessAt);

        Assert.Equal(DueVerdict.NotYet, ScheduleDecision.Evaluate(
            Assert.Single(await store.ReadSchedulesAsync(CancellationToken.None), schedule => schedule.Id == "a-broken"),
            failed,
            clock.GetUtcNow()).Verdict);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private FileSystemScheduleStore Store() => new(_directory);

    private Task WriteIntervalScheduleAsync(string id, TimeSpan period) => WriteScheduleAsync(id, $$"""
        {
          "schema": "fortiq.backup-schedule",
          "version": 1,
          "id": "{{id}}",
          "repository": "C:/repository",
          "kit": "C:/kit",
          "source": "C:/source",
          "sourceStableId": "workstation:documents",
          "recurrence": { "kind": "interval", "period": "{{period}}" }
        }
        """);

    private async Task WriteScheduleAsync(string id, string json)
    {
        var directory = Path.Combine(_directory, "schedules");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, id + ".json"), json);
    }

    private sealed class FakeClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class RecordingBackup(string snapshotId, string? failFor = null) : IScheduledBackup
    {
        private readonly List<BackupSchedule> _ran = [];

        internal IReadOnlyList<BackupSchedule> Ran => _ran;

        public Task<BackupReceipt> RunAsync(BackupSchedule schedule, CancellationToken cancellationToken)
        {
            if (schedule.Id == failFor)
            {
                return Task.FromException<BackupReceipt>(new IOException("the repository was unreachable"));
            }

            _ran.Add(schedule);
            return Task.FromResult(new BackupReceipt(Guid.NewGuid(), RepositoryId.Create(), snapshotId));
        }
    }
}
