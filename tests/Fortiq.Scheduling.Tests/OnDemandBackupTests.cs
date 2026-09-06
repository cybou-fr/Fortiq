using Fortiq.Application;
using Fortiq.Domain;
using Fortiq.Scheduling;

namespace Fortiq.Scheduling.Tests;

/// <summary>
/// A backup somebody asked for, rather than one a clock reached. It runs whether or not the schedule
/// is due, and everything after that is the scheduled path: the same runner, the same state, one
/// history.
/// </summary>
public sealed class OnDemandBackupTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "fortiq-on-demand-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ABackupRunsWhenAskedForEvenThoughItIsNotDue()
    {
        await WriteScheduleAsync("documents", enabled: true);
        var store = Store();
        var backup = new RecordingBackup("snapshot-1");
        var runner = new ScheduledBackupRunner(store, backup, new FakeClock(DateTimeOffset.UtcNow));

        // Nothing is due: the schedule runs daily and has just run.
        await store.WriteStateAsync(
            new ScheduleState("documents", LastAttemptAt: DateTimeOffset.UtcNow, LastSuccessAt: DateTimeOffset.UtcNow),
            CancellationToken.None);
        Assert.DoesNotContain(await runner.RunDueAsync(CancellationToken.None), outcome => outcome.SnapshotId is not null);

        var result = await runner.RunNowAsync("documents", CancellationToken.None);

        Assert.Null(result.Failure);
        Assert.Equal("snapshot-1", result.SnapshotId);
        Assert.Equal("documents", Assert.Single(backup.Ran).Id);
    }

    [Fact]
    public async Task AManualRunIsRecordedInTheSchedulesOwnHistory()
    {
        await WriteScheduleAsync("documents", enabled: true);
        var store = Store();
        var runner = new ScheduledBackupRunner(store, new RecordingBackup("snapshot-1"));

        await runner.RunNowAsync("documents", CancellationToken.None);

        // The point of writing it here: a manual backup counts as the occurrence, so the next pass
        // does not immediately repeat work that has just been done.
        var state = await store.ReadStateAsync("documents", CancellationToken.None);
        Assert.Equal("snapshot-1", state.LastSnapshotId);
        Assert.NotNull(state.LastSuccessAt);
        Assert.Null(state.LastFailure);
    }

    [Fact]
    public async Task AFailedManualRunIsReportedAndRecordedRatherThanThrown()
    {
        await WriteScheduleAsync("documents", enabled: true);
        var store = Store();
        var runner = new ScheduledBackupRunner(store, new RecordingBackup("snapshot-1", failFor: "documents"));

        var result = await runner.RunNowAsync("documents", CancellationToken.None);

        Assert.Null(result.SnapshotId);
        Assert.Contains("unreachable", result.Failure, StringComparison.Ordinal);
        var state = await store.ReadStateAsync("documents", CancellationToken.None);
        Assert.Contains("unreachable", state.LastFailure!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APausedScheduleStillRunsWhenSomebodyAsksItTo()
    {
        // Pausing says "not on your own". It is a statement about the clock, not about the person:
        // somebody who turned the nightly run off must still be able to back the folder up, and the
        // button that proves recovery already works on a paused source.
        await WriteScheduleAsync("documents", enabled: false);
        var backup = new RecordingBackup("snapshot-1");
        var runner = new ScheduledBackupRunner(Store(), backup);

        var result = await runner.RunNowAsync("documents", CancellationToken.None);

        Assert.Null(result.Failure);
        Assert.Equal("snapshot-1", result.SnapshotId);
        Assert.Equal("documents", Assert.Single(backup.Ran).Id);
    }

    [Fact]
    public async Task APausedScheduleStillNeverRunsOnItsOwn()
    {
        await WriteScheduleAsync("documents", enabled: false);
        var backup = new RecordingBackup("snapshot-1");
        var runner = new ScheduledBackupRunner(Store(), backup);

        await runner.RunDueAsync(CancellationToken.None);

        Assert.Empty(backup.Ran);
    }

    [Fact]
    public async Task AnUnknownScheduleIsReportedWithoutRunningAnythingElse()
    {
        await WriteScheduleAsync("documents", enabled: true);
        var backup = new RecordingBackup("snapshot-1");
        var runner = new ScheduledBackupRunner(Store(), backup);

        var result = await runner.RunNowAsync("photos", CancellationToken.None);

        Assert.NotNull(result.Failure);
        Assert.Equal("photos", result.ScheduleId);
        Assert.Empty(backup.Ran);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private FileSystemScheduleStore Store() => new(_directory);

    private async Task WriteScheduleAsync(string id, bool enabled)
    {
        var directory = Path.Combine(_directory, "schedules");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, id + ".json"), $$"""
            {
              "schema": "fortiq.backup-schedule",
              "version": 1,
              "id": "{{id}}",
              "repository": "C:/repository",
              "kit": "C:/kit",
              "source": "C:/source",
              "sourceStableId": "workstation:{{id}}",
              "enabled": {{(enabled ? "true" : "false")}},
              "recurrence": { "kind": "dailyAt", "timeOfDay": "02:30", "timeZone": "UTC" }
            }
            """);
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
