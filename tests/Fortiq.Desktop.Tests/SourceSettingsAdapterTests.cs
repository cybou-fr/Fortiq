using Fortiq.Application;
using Fortiq.Desktop;
using Fortiq.Desktop.ViewModels;
using Fortiq.Scheduling;

namespace Fortiq.Desktop.Tests;

/// <summary>
/// The translation between what a screen can say - whole hours, whole days, plain counts - and what
/// the scheduling domain means by it. A mistake here is a schedule that runs at the wrong time or a
/// retention policy that deletes more than somebody asked for.
/// </summary>
public sealed class SourceSettingsAdapterTests
{
    [Fact]
    public void ASchedulesTimeAndDrillArriveOnTheScreenAsTheyWereWritten()
    {
        var schedule = Schedule(
            recurrence: new DailyAt(new TimeOnly(21, 45), TimeZoneInfo.Utc),
            drill: new EveryInterval(TimeSpan.FromDays(3)));

        var settings = SourceSettingsAdapter.SettingsOf(schedule);

        Assert.Equal(21, settings.BackupHour);
        Assert.Equal(45, settings.BackupMinute);
        Assert.Equal(3, settings.DrillEveryDays);
    }

    [Fact]
    public void ARecurrenceThisScreenHasNoFieldForStillOpens()
    {
        // Somebody may have written an interval by hand. Refusing to show the screen would leave them
        // no way to change anything else about that source either.
        var schedule = Schedule(recurrence: new EveryInterval(TimeSpan.FromHours(6)));

        var settings = SourceSettingsAdapter.SettingsOf(schedule);

        Assert.Equal(2, settings.BackupHour);
        Assert.Equal(30, settings.BackupMinute);
    }

    [Fact]
    public void ADrillIntervalShorterThanADayIsStillAtLeastOneDayOnTheScreen()
    {
        // The screen counts in days, and rounding six hours down to zero would read as "off" - which
        // is the opposite of what that schedule says.
        var schedule = Schedule(drill: new EveryInterval(TimeSpan.FromHours(6)));

        Assert.Equal(1, SourceSettingsAdapter.SettingsOf(schedule).DrillEveryDays);
    }

    [Fact]
    public void ARetentionRecurrenceWithNoPolicyIsNotRetention()
    {
        // The reader treats half-configured retention as unconfigured; the screen has to agree, or a
        // save would turn a rule nothing applied into a rule that deletes.
        var schedule = Schedule(retentionRecurrence: new EveryInterval(TimeSpan.FromDays(1)), retention: null);

        var settings = SourceSettingsAdapter.SettingsOf(schedule);

        Assert.False(settings.RetentionConfigured);
        Assert.Null(settings.KeepDaily);
    }

    [Fact]
    public void RetentionOffOnTheScreenIsNoPolicyAtAll()
    {
        var settings = new SourceSettings(true, 2, 30, 7, null, null, null, Prune: true);

        var preferences = SourceSettingsAdapter.PreferencesOf(settings);

        Assert.Null(preferences.Retention);
    }

    [Fact]
    public void WhatTheScreenShowsSurvivesARoundTrip()
    {
        var original = new SourceSettings(false, 5, 15, 14, KeepDaily: 7, KeepWeekly: 4, KeepMonthly: 12, Prune: true);

        var preferences = SourceSettingsAdapter.PreferencesOf(original);
        var schedule = Schedule(
            enabled: preferences.Enabled,
            recurrence: new DailyAt(preferences.BackupTime, TimeZoneInfo.Local),
            drill: preferences.DrillEvery is { } every ? new EveryInterval(every) : null,
            retentionRecurrence: new EveryInterval(TimeSpan.FromDays(1)),
            retention: preferences.Retention,
            prune: preferences.Prune);

        Assert.Equal(original, SourceSettingsAdapter.SettingsOf(schedule));
    }

    [Fact]
    public void AnHourOutsideADayIsBroughtBackIntoOne()
    {
        var preferences = SourceSettingsAdapter.PreferencesOf(new SourceSettings(true, 99, 99, null, null, null, null, false));

        Assert.Equal(new TimeOnly(23, 59), preferences.BackupTime);
    }

    private static BackupSchedule Schedule(
        bool enabled = true,
        Recurrence? recurrence = null,
        Recurrence? drill = null,
        Recurrence? retentionRecurrence = null,
        RetentionPolicy? retention = null,
        PruneMode prune = PruneMode.ForgetOnly) =>
        new(
            "documents",
            @"C:\repository",
            @"C:\kit",
            @"C:\source",
            "workstation:documents",
            recurrence ?? new DailyAt(new TimeOnly(2, 30), TimeZoneInfo.Utc),
            Enabled: enabled,
            DrillRecurrence: drill,
            RetentionRecurrence: retentionRecurrence,
            Retention: retention,
            Prune: prune);
}
