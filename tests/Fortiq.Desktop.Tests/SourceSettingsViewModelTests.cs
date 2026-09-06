using Fortiq.Desktop.ViewModels;

namespace Fortiq.Desktop.Tests;

/// <summary>
/// One source's own settings. Everything here was previously either a constant nobody could reach or
/// a file in a directory a standard account cannot write to.
/// </summary>
public sealed class SourceSettingsViewModelTests
{
    [Fact]
    public async Task TheScreenOpensOnWhatTheScheduleActuallySays()
    {
        var store = new FakeStore(Settings(backupHour: 21, backupMinute: 15, drillEveryDays: 3));
        var model = new SourceSettingsViewModel(store, "repo-1", "Documents");

        await model.LoadAsync(CancellationToken.None);

        Assert.Equal(21, model.BackupHour);
        Assert.Equal(15, model.BackupMinute);
        Assert.Equal(3, model.DrillEveryDays);
        Assert.False(model.RetentionEnabled);
    }

    [Fact]
    public async Task ASourceNoScheduleGovernsSaysSoRatherThanShowingDefaults()
    {
        // Defaults on this screen would read as settings somebody had chosen, for a source nothing is
        // backing up.
        var model = new SourceSettingsViewModel(new FakeStore(null), "repo-1", "Documents");

        await model.LoadAsync(CancellationToken.None);

        Assert.Null(model.Details);
        Assert.Contains("not being backed up", model.Failure!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TurningRetentionOnChoosesAPolicyThatKeepsSomething()
    {
        // A switch that turned retention on and left the counts empty would be an instruction to
        // delete every backup.
        var store = new FakeStore(Settings());
        var model = new SourceSettingsViewModel(store, "repo-1", "Documents");
        await model.LoadAsync(CancellationToken.None);

        model.SetRetentionEnabled(true);

        Assert.True(model.RetentionEnabled);
        Assert.NotNull(model.KeepDaily);
        Assert.NotNull(model.KeepWeekly);
        Assert.NotNull(model.KeepMonthly);
    }

    [Fact]
    public async Task TurningRetentionOffAlsoStopsPruning()
    {
        var model = new SourceSettingsViewModel(new FakeStore(Settings()), "repo-1", "Documents");
        await model.LoadAsync(CancellationToken.None);
        model.SetRetentionEnabled(true);
        model.Prune = true;

        model.SetRetentionEnabled(false);

        Assert.False(model.RetentionEnabled);
        Assert.False(model.Prune);
    }

    [Fact]
    public async Task ACountOfZeroIsTheOffSwitchSaidBadly()
    {
        var model = new SourceSettingsViewModel(new FakeStore(Settings()), "repo-1", "Documents");
        await model.LoadAsync(CancellationToken.None);

        model.DrillEveryDays = 0;
        model.KeepDaily = -3;

        Assert.Null(model.DrillEveryDays);
        Assert.Null(model.KeepDaily);
    }

    [Fact]
    public async Task SavingWritesWhatTheScreenShows()
    {
        var store = new FakeStore(Settings());
        var model = new SourceSettingsViewModel(store, "repo-1", "Documents");
        await model.LoadAsync(CancellationToken.None);

        model.BackupHour = 5;
        model.BackupMinute = 45;
        model.Enabled = false;
        await model.SaveAsync(CancellationToken.None);

        Assert.Equal(5, store.Saved!.BackupHour);
        Assert.Equal(45, store.Saved.BackupMinute);
        Assert.False(store.Saved.Enabled);
        Assert.Equal("Saved.", model.Saved);
    }

    [Fact]
    public async Task WhatTheScreenShowsAfterASaveIsWhatWasKeptRatherThanWhatWasAsked()
    {
        // The store may keep something other than what the screen sent. Showing the request would
        // describe a schedule that does not exist on disk.
        var store = new FakeStore(Settings(), keepsInstead: Settings(backupHour: 2, backupMinute: 30));
        var model = new SourceSettingsViewModel(store, "repo-1", "Documents");
        await model.LoadAsync(CancellationToken.None);

        model.BackupHour = 23;
        await model.SaveAsync(CancellationToken.None);

        Assert.Equal(2, model.BackupHour);
    }

    [Fact]
    public async Task ARefusedSaveIsReportedAndChangesNothingOnTheScreen()
    {
        var store = new FakeStore(Settings(), failure: new UnauthorizedAccessException("Access to the path is denied."));
        var model = new SourceSettingsViewModel(store, "repo-1", "Documents");
        await model.LoadAsync(CancellationToken.None);

        model.BackupHour = 23;
        await model.SaveAsync(CancellationToken.None);

        Assert.NotNull(model.Failure);
        Assert.DoesNotContain("Exception", model.Failure, StringComparison.Ordinal);
        Assert.Null(model.Saved);
        Assert.Equal(23, model.BackupHour);
    }

    [Fact]
    public async Task RemovingSaysSoOnlyWhenItWorked()
    {
        var store = new FakeStore(Settings(), failure: new IOException("the file is in use"));
        var model = new SourceSettingsViewModel(store, "repo-1", "Documents");
        await model.LoadAsync(CancellationToken.None);

        await model.RemoveAsync(CancellationToken.None);

        Assert.False(model.Removed);
        Assert.NotNull(model.Failure);
    }

    [Fact]
    public async Task RemovingASourceMarksItGone()
    {
        var store = new FakeStore(Settings());
        var model = new SourceSettingsViewModel(store, "repo-1", "Documents");
        await model.LoadAsync(CancellationToken.None);

        await model.RemoveAsync(CancellationToken.None);

        Assert.True(model.Removed);
        Assert.Equal("repo-1", store.RemovedId);
    }

    [Fact]
    public async Task ClearingALockSaysThatBackupsCanRunAgain()
    {
        var store = new FakeStore(Settings());
        var model = new SourceSettingsViewModel(store, "repo-1", "Documents");
        await model.LoadAsync(CancellationToken.None);

        await model.ClearLockAsync(CancellationToken.None);

        Assert.Equal("repo-1", store.ClearedLock);
        Assert.Contains("can run again", model.Saved!, StringComparison.Ordinal);
        Assert.Null(model.Failure);
    }

    [Fact]
    public async Task ARefusedLockClearanceIsReportedRatherThanClaimedAsDone()
    {
        // The service refuses while one of its own runs holds the repository. That is the guard
        // working, and somebody who was told it succeeded would go on to wonder why backups still fail.
        var store = new FakeStore(Settings(), failure: new InvalidOperationException("Fortiq is working on this repository right now."));
        var model = new SourceSettingsViewModel(store, "repo-1", "Documents");
        await model.LoadAsync(CancellationToken.None);

        await model.ClearLockAsync(CancellationToken.None);

        Assert.Null(model.Saved);
        Assert.Contains("working on this repository", model.Failure!, StringComparison.Ordinal);
    }

    [Fact]
    public void NothingCanBeSavedBeforeAnythingHasBeenRead()
    {
        var store = new FakeStore(Settings());
        var model = new SourceSettingsViewModel(store, "repo-1", "Documents");

        Assert.Null(model.Details);
        Assert.Null(store.Saved);
    }

    private static SourceSettings Settings(
        bool enabled = true,
        int backupHour = 2,
        int backupMinute = 30,
        int? drillEveryDays = 7) =>
        new(enabled, backupHour, backupMinute, drillEveryDays, null, null, null, false);

    private sealed class FakeStore(SourceSettings? settings, SourceSettings? keepsInstead = null, Exception? failure = null)
        : ISourceSettingsStore
    {
        private SourceSettings? _settings = settings;

        internal SourceSettings? Saved { get; private set; }

        internal string? RemovedId { get; private set; }

        internal string? ClearedLock { get; private set; }

        public Task<SourceDetails?> ReadAsync(string repositoryId, CancellationToken cancellationToken) =>
            Task.FromResult(_settings is null
                ? null
                : new SourceDetails(repositoryId, "documents", @"C:\source", @"C:\repository", @"C:\kit", _settings));

        public Task SaveAsync(string repositoryId, SourceSettings settings, CancellationToken cancellationToken)
        {
            if (failure is not null)
            {
                return Task.FromException(failure);
            }

            Saved = settings;
            _settings = keepsInstead ?? settings;
            return Task.CompletedTask;
        }

        public Task ClearLockAsync(string repositoryId, CancellationToken cancellationToken)
        {
            if (failure is not null)
            {
                return Task.FromException(failure);
            }

            ClearedLock = repositoryId;
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string repositoryId, CancellationToken cancellationToken)
        {
            if (failure is not null)
            {
                return Task.FromException(failure);
            }

            RemovedId = repositoryId;
            _settings = null;
            return Task.CompletedTask;
        }
    }
}
