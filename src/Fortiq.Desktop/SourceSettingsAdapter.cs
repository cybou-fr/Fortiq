using System.Runtime.Versioning;
using Fortiq.Application;
using Fortiq.Desktop.ViewModels;
using Fortiq.Operations;
using Fortiq.Scheduling;

namespace Fortiq.Desktop;

/// <summary>
/// Reads one source's schedule from disk and changes it through whichever half of the product is
/// allowed to write.
/// </summary>
/// <remarks>
/// Reading is direct in both modes. An installed machine grants every account read access to the
/// state directory, which is what lets this screen open and show a schedule without a prompt; writing
/// is the service's, and asking it is the only way an installed desktop changes anything. Portable
/// owns its own state beside the executable and writes it itself, which is what portable means.
/// </remarks>
public sealed class SourceSettingsAdapter : ISourceSettingsStore
{
    private readonly FileSystemScheduleStore _schedules;
    private readonly IServiceIpcClient? _serviceClient;
    private readonly Func<string, string, SourceSettings?, CancellationToken, Task<bool>>? _elevate;
    private readonly Func<BackupSchedule, CancellationToken, Task>? _clearLocalLock;

    public SourceSettingsAdapter(FileSystemScheduleStore schedules, IServiceIpcClient? serviceClient = null,
        Func<BackupSchedule, CancellationToken, Task>? clearLocalLock = null,
        Func<string, string, SourceSettings?, CancellationToken, Task<bool>>? elevate = null)
    {
        _schedules = schedules ?? throw new ArgumentNullException(nameof(schedules));
        _serviceClient = serviceClient;
        _clearLocalLock = clearLocalLock;
        _elevate = elevate;
    }

    public async Task<SourceDetails?> ReadAsync(string repositoryId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryId);

        var schedule = await ScheduleLookup.FindForRepositoryAsync(_schedules, repositoryId, cancellationToken);
        if (schedule is null)
        {
            return null;
        }

        return new SourceDetails(
            repositoryId,
            schedule.Id,
            schedule.SourcePath,
            schedule.RepositoryLocation,
            schedule.KitDirectory,
            SettingsOf(schedule));
    }

    [SupportedOSPlatform("windows")]
    public async Task SaveAsync(string repositoryId, SourceSettings settings, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryId);
        ArgumentNullException.ThrowIfNull(settings);

        if (_serviceClient is not null)
        {
            await RequireServiceAsync(cancellationToken);
            if (_elevate is not null && await _elevate("updateSchedule", repositoryId, settings, cancellationToken)) return;
            await _serviceClient.UpdateScheduleAsync(repositoryId, settings, cancellationToken);
            return;
        }

        var schedule = await Required(repositoryId, cancellationToken);
        await _schedules.UpdateAsync(schedule.Id, PreferencesOf(settings), cancellationToken);
    }

    [SupportedOSPlatform("windows")]
    public async Task RemoveAsync(string repositoryId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryId);

        if (_serviceClient is not null)
        {
            await RequireServiceAsync(cancellationToken);
            if (_elevate is not null && await _elevate("removeSchedule", repositoryId, null, cancellationToken)) return;
            await _serviceClient.RemoveScheduleAsync(repositoryId, cancellationToken);
            return;
        }

        var schedule = await Required(repositoryId, cancellationToken);
        await _schedules.RemoveAsync(schedule.Id, cancellationToken);
    }

    [SupportedOSPlatform("windows")]
    public async Task ClearLockAsync(string repositoryId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryId);

        if (_serviceClient is null)
        {
            if (_clearLocalLock is null)
                throw new InvalidOperationException("Local lock recovery is unavailable in this session.");
            await _clearLocalLock(await Required(repositoryId, cancellationToken), cancellationToken);
            return;
        }

        await RequireServiceAsync(cancellationToken);
        if (_elevate is not null && await _elevate("clearLock", repositoryId, null, cancellationToken)) return;
        await _serviceClient.ClearLockAsync(repositoryId, cancellationToken);
    }

    /// <summary>The schedule as a screen states it: whole hours, whole days, plain counts.</summary>
    public static SourceSettings SettingsOf(BackupSchedule schedule)
    {
        ArgumentNullException.ThrowIfNull(schedule);

        // Values the GUI cannot represent are marked read-only and preserved by the save intent.
        var time = schedule.Recurrence is DailyAt daily ? daily.TimeOfDay : new TimeOnly(2, 30);
        var drill = schedule.DrillRecurrence is EveryInterval interval ? (int?)Math.Max(1, (int)interval.Period.TotalDays) : null;

        return new SourceSettings(
            schedule.Enabled,
            time.Hour,
            time.Minute,
            drill,
            schedule.RetentionConfigured ? schedule.Retention?.KeepDaily : null,
            schedule.RetentionConfigured ? schedule.Retention?.KeepWeekly : null,
            schedule.RetentionConfigured ? schedule.Retention?.KeepMonthly : null,
            schedule.Prune == PruneMode.ForgetAndPrune,
            UpdateBackupTime: schedule.Recurrence is DailyAt,
            UpdateDrill: schedule.DrillRecurrence is null || schedule.DrillRecurrence is EveryInterval exact
                && exact.Period.TotalDays >= 1 && exact.Period.TotalDays <= int.MaxValue
                && exact.Period.TotalDays == Math.Truncate(exact.Period.TotalDays));
    }

    /// <summary>The screen's numbers, back in the scheduling domain's own vocabulary.</summary>
    public static SchedulePreferences PreferencesOf(SourceSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var retention = settings.RetentionConfigured
            ? new RetentionPolicy(
                KeepDaily: settings.KeepDaily,
                KeepWeekly: settings.KeepWeekly,
                KeepMonthly: settings.KeepMonthly)
            : null;

        return new SchedulePreferences(
            settings.Enabled,
            new TimeOnly(Math.Clamp(settings.BackupHour, 0, 23), Math.Clamp(settings.BackupMinute, 0, 59)),
            settings.DrillEveryDays is { } days and > 0 ? TimeSpan.FromDays(days) : null,
            retention,
            settings.Prune ? PruneMode.ForgetAndPrune : PruneMode.ForgetOnly,
            settings.UpdateBackupTime, settings.UpdateDrill, settings.UpdateRetention);
    }

    private async Task RequireServiceAsync(CancellationToken cancellationToken)
    {
        if (_serviceClient is null || await _serviceClient.IsServiceAvailableAsync(cancellationToken))
        {
            return;
        }

        throw new InvalidOperationException(
            "The Fortiq service is unavailable, so this schedule cannot be changed. Start the Fortiq service and try again.");
    }

    private async Task<BackupSchedule> Required(string repositoryId, CancellationToken cancellationToken) =>
        await ScheduleLookup.FindForRepositoryAsync(_schedules, repositoryId, cancellationToken)
        ?? throw new InvalidOperationException(
            "No schedule on this machine governs that source, so there is nothing to change.");
}
