using Fortiq.Application;
using Fortiq.Scheduling;

namespace Fortiq.Scheduling.Tests;

/// <summary>
/// Changing a schedule from the application. Until this existed the backup ran at whatever time
/// provisioning wrote, retention was absent because nothing wrote it, and the only way to alter
/// either was to hand-edit a file in a directory a standard account cannot write to.
/// </summary>
public sealed class ScheduleEditingTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "fortiq-editing-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task TheBackupTimeAndPausedStateAreWrittenAndReadBack()
    {
        await WriteDefaultAsync();
        var store = Store();

        await store.UpdateAsync(
            "documents",
            new SchedulePreferences(false, new TimeOnly(21, 45), TimeSpan.FromDays(3), null, PruneMode.ForgetOnly),
            CancellationToken.None);

        var schedule = Assert.Single(await store.ReadSchedulesAsync(CancellationToken.None));
        Assert.False(schedule.Enabled);
        Assert.Equal(new TimeOnly(21, 45), Assert.IsType<DailyAt>(schedule.Recurrence).TimeOfDay);
        Assert.Equal(TimeSpan.FromDays(3), Assert.IsType<EveryInterval>(schedule.DrillRecurrence).Period);
    }

    [Fact]
    public async Task RetentionArrivesWithBothHalvesOrNotAtAll()
    {
        // A policy without a recurrence is a rule nothing applies, and a recurrence without a policy
        // is a schedule for deleting snapshots by no rule. The reader treats either as unconfigured,
        // so the writer must never produce one.
        await WriteDefaultAsync();
        var store = Store();

        await store.UpdateAsync(
            "documents",
            new SchedulePreferences(
                true,
                new TimeOnly(2, 30),
                TimeSpan.FromDays(7),
                new RetentionPolicy(KeepDaily: 7, KeepWeekly: 4, KeepMonthly: 12),
                PruneMode.ForgetAndPrune),
            CancellationToken.None);

        var schedule = Assert.Single(await store.ReadSchedulesAsync(CancellationToken.None));
        Assert.True(schedule.RetentionConfigured);
        Assert.Equal(7, schedule.Retention!.KeepDaily);
        Assert.Equal(PruneMode.ForgetAndPrune, schedule.Prune);
    }

    [Fact]
    public async Task TurningRetentionOffLeavesNothingALaterVersionCouldReadAsRetention()
    {
        await WriteDefaultAsync();
        var store = Store();
        await store.UpdateAsync(
            "documents",
            new SchedulePreferences(true, new TimeOnly(2, 30), null, new RetentionPolicy(KeepDaily: 7), PruneMode.ForgetOnly),
            CancellationToken.None);

        await store.UpdateAsync(
            "documents",
            new SchedulePreferences(true, new TimeOnly(2, 30), null, null, PruneMode.ForgetOnly),
            CancellationToken.None);

        var schedule = Assert.Single(await store.ReadSchedulesAsync(CancellationToken.None));
        Assert.False(schedule.RetentionConfigured);
        Assert.Null(schedule.Retention);
        Assert.Null(schedule.RetentionRecurrence);
        Assert.DoesNotContain("keepDaily", await File.ReadAllTextAsync(SchedulePath()), StringComparison.Ordinal);
    }

    [Fact]
    public async Task APolicyThatKeepsNothingIsRefusedBeforeAnythingIsWritten()
    {
        await WriteDefaultAsync();
        var store = Store();
        var before = await File.ReadAllTextAsync(SchedulePath());

        await Assert.ThrowsAsync<InvalidDataException>(() => store.UpdateAsync(
            "documents",
            new SchedulePreferences(true, new TimeOnly(2, 30), null, new RetentionPolicy(), PruneMode.ForgetOnly),
            CancellationToken.None));

        Assert.Equal(before, await File.ReadAllTextAsync(SchedulePath()));
    }

    [Fact]
    public async Task FieldsThisScreenHasNoIdeaAboutSurviveASave()
    {
        // A schedule file is something a person may have written or extended by hand. Rewriting it
        // from the fields one screen knows about would discard the rest without telling anybody.
        await WriteDefaultAsync(extra: "\"objectsNobodyHereUnderstands\": { \"kept\": true },");
        var store = Store();

        await store.UpdateAsync(
            "documents",
            new SchedulePreferences(true, new TimeOnly(4, 0), null, null, PruneMode.ForgetOnly),
            CancellationToken.None);

        Assert.Contains("objectsNobodyHereUnderstands", await File.ReadAllTextAsync(SchedulePath()), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AHandWrittenWeekdayRestrictionIsNotThrownAwayByChangingTheTime()
    {
        await WriteDefaultAsync(days: ", \"days\": [\"Saturday\", \"Sunday\"]");
        var store = Store();

        await store.UpdateAsync(
            "documents",
            new SchedulePreferences(true, new TimeOnly(5, 15), null, null, PruneMode.ForgetOnly),
            CancellationToken.None);

        var recurrence = Assert.IsType<DailyAt>(Assert.Single(await store.ReadSchedulesAsync(CancellationToken.None)).Recurrence);
        Assert.Equal(new TimeOnly(5, 15), recurrence.TimeOfDay);
        Assert.Equal([DayOfWeek.Saturday, DayOfWeek.Sunday], recurrence.Days);
    }

    [Fact]
    public async Task RemovingAScheduleTakesItsHistoriesAndNothingElse()
    {
        await WriteDefaultAsync();
        var store = Store();
        await store.WriteStateAsync(new ScheduleState("documents", LastSuccessAt: DateTimeOffset.UtcNow), CancellationToken.None);
        await store.WriteStateAsync(new ScheduleState("documents.drill", LastSuccessAt: DateTimeOffset.UtcNow), CancellationToken.None);

        // Something that stands for what has already been backed up: removing a schedule must not
        // touch it. Somebody who stops backing a folder up has not asked to lose the backups they have.
        var repositoryMarker = Path.Combine(_directory, "repository-stand-in.txt");
        await File.WriteAllTextAsync(repositoryMarker, "snapshots");

        await store.RemoveAsync("documents", CancellationToken.None);

        Assert.Empty(await store.ReadSchedulesAsync(CancellationToken.None));
        Assert.Null((await store.ReadStateAsync("documents", CancellationToken.None)).LastSuccessAt);
        Assert.Null((await store.ReadStateAsync("documents.drill", CancellationToken.None)).LastSuccessAt);
        Assert.True(File.Exists(repositoryMarker));
    }

    [Fact]
    public async Task ChangingAScheduleThatIsNotThereSaysSo()
    {
        await WriteDefaultAsync();

        await Assert.ThrowsAsync<FileNotFoundException>(() => Store().UpdateAsync(
            "photos",
            new SchedulePreferences(true, new TimeOnly(2, 30), null, null, PruneMode.ForgetOnly),
            CancellationToken.None));
    }

    [Fact]
    public async Task AScheduleIdThatCouldEscapeTheDirectoryIsRefused()
    {
        await WriteDefaultAsync();

        await Assert.ThrowsAsync<InvalidDataException>(() => Store().RemoveAsync(
            "../../windows/system32/config",
            CancellationToken.None));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private FileSystemScheduleStore Store() => new(_directory);

    private string SchedulePath() => Path.Combine(_directory, "schedules", "documents.json");

    private async Task WriteDefaultAsync(string extra = "", string days = "")
    {
        var directory = Path.Combine(_directory, "schedules");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "documents.json"), $$"""
            {
              "schema": "fortiq.backup-schedule",
              "version": 1,
              "id": "documents",
              "repository": "C:/repository",
              "kit": "C:/kit",
              "source": "C:/source",
              "sourceStableId": "workstation:documents",
              {{extra}}
              "recurrence": { "kind": "dailyAt", "timeOfDay": "02:30", "timeZone": "UTC"{{days}} }
            }
            """);
    }
}
