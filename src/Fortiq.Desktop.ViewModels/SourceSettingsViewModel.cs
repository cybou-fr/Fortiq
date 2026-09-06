using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Fortiq.Desktop.ViewModels;

/// <summary>
/// What a person may change about one protected source.
/// </summary>
/// <remarks>
/// Plain numbers rather than the scheduling domain's own types, because this record travels from a
/// screen through an adapter and, on an installed machine, across a process boundary. Null means off
/// in both places it can be null: no restore drills, and no retention - which is to say keep
/// everything, forever, the only safe default for somebody else's backups.
/// </remarks>
public sealed record SourceSettings(
    bool Enabled,
    int BackupHour,
    int BackupMinute,
    int? DrillEveryDays,
    int? KeepDaily,
    int? KeepWeekly,
    int? KeepMonthly,
    bool Prune)
{
    /// <summary>Whether anything at all is being forgotten.</summary>
    public bool RetentionConfigured => KeepDaily is not null || KeepWeekly is not null || KeepMonthly is not null;
}

/// <summary>One protected source, as its own screen shows it.</summary>
public sealed record SourceDetails(
    string RepositoryId,
    string ScheduleId,
    string SourcePath,
    string RepositoryLocation,
    string KitDirectory,
    SourceSettings Settings);

/// <summary>
/// Reads and changes what one source's schedule says.
/// </summary>
/// <remarks>
/// Reading and writing are deliberately not the same privilege. The schedules directory is readable
/// by any account on the machine and writable only by the service, so showing these settings needs
/// nothing while changing them goes through the service - which is why they are one interface with
/// two very different implementations behind it rather than two screens.
/// </remarks>
public interface ISourceSettingsStore
{
    Task<SourceDetails?> ReadAsync(string repositoryId, CancellationToken cancellationToken);

    Task SaveAsync(string repositoryId, SourceSettings settings, CancellationToken cancellationToken);

    Task RemoveAsync(string repositoryId, CancellationToken cancellationToken);
}

/// <summary>
/// One source's own screen: when it is backed up, how often recovery is proven, what may be
/// forgotten, and how to stop.
/// </summary>
/// <remarks>
/// Everything here used to be a file somebody had to find in %ProgramData% and edit by hand, or a
/// constant nobody could reach at all: the backup ran at 02:30 because provisioning wrote 02:30, and
/// retention was absent because nothing wrote it, so every repository grew forever. A backup product
/// where the schedule is not a setting is a backup product with one schedule.
/// </remarks>
public sealed class SourceSettingsViewModel : INotifyPropertyChanged
{
    private readonly ISourceSettingsStore _store;
    private readonly string _repositoryId;

    private SourceDetails? _details;
    private bool _busy;
    private string? _failure;
    private string? _saved;
    private bool _removed;

    private bool _enabled = true;
    private int _backupHour = 2;
    private int _backupMinute = 30;
    private int? _drillEveryDays = 7;
    private int? _keepDaily;
    private int? _keepWeekly;
    private int? _keepMonthly;
    private bool _prune;

    public SourceSettingsViewModel(ISourceSettingsStore store, string repositoryId, string title)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryId);
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _repositoryId = repositoryId;
        Title = title;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The source's name, as the rest of the application already calls it.</summary>
    public string Title { get; }

    public bool Busy { get => _busy; private set => Set(ref _busy, value); }

    public string? Failure { get => _failure; private set => Set(ref _failure, value); }

    /// <summary>Set after a successful save, so the screen can say so rather than only stop being busy.</summary>
    public string? Saved { get => _saved; private set => Set(ref _saved, value); }

    /// <summary>True once this source is no longer protected; the screen closes on it.</summary>
    public bool Removed { get => _removed; private set => Set(ref _removed, value); }

    /// <summary>Null until the schedule has been read. A screen with no schedule can change nothing.</summary>
    public SourceDetails? Details { get => _details; private set => Set(ref _details, value); }

    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }

    public int BackupHour { get => _backupHour; set => Set(ref _backupHour, Math.Clamp(value, 0, 23)); }

    public int BackupMinute { get => _backupMinute; set => Set(ref _backupMinute, Math.Clamp(value, 0, 59)); }

    /// <summary>Null turns unattended restore drills off.</summary>
    public int? DrillEveryDays { get => _drillEveryDays; set => Set(ref _drillEveryDays, Positive(value)); }

    public int? KeepDaily { get => _keepDaily; set => Set(ref _keepDaily, Positive(value)); }

    public int? KeepWeekly { get => _keepWeekly; set => Set(ref _keepWeekly, Positive(value)); }

    public int? KeepMonthly { get => _keepMonthly; set => Set(ref _keepMonthly, Positive(value)); }

    public bool Prune { get => _prune; set => Set(ref _prune, value); }

    /// <summary>
    /// Whether retention is on at all. Off means every snapshot is kept forever.
    /// </summary>
    public bool RetentionEnabled => KeepDaily is not null || KeepWeekly is not null || KeepMonthly is not null;

    /// <summary>
    /// Turns retention on with a conservative policy, or off entirely.
    /// </summary>
    /// <remarks>
    /// On means a policy that keeps something: a daily for a week, a weekly for a month, a monthly for
    /// a year. A checkbox that turned retention on and left every count empty would be a policy that
    /// keeps nothing, which is deletion - so the switch chooses the numbers and the person adjusts
    /// them, rather than the other way round.
    /// </remarks>
    public void SetRetentionEnabled(bool enabled)
    {
        if (enabled == RetentionEnabled)
        {
            return;
        }

        if (enabled)
        {
            KeepDaily = 7;
            KeepWeekly = 4;
            KeepMonthly = 12;
        }
        else
        {
            KeepDaily = null;
            KeepWeekly = null;
            KeepMonthly = null;
            Prune = false;
        }

        OnPropertyChanged(nameof(RetentionEnabled));
    }

    /// <summary>Reads what this source's schedule currently says.</summary>
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        if (Busy) return;
        Busy = true;
        Failure = null;
        try
        {
            var details = await _store.ReadAsync(_repositoryId, cancellationToken);
            if (details is null)
            {
                // A repository the health report knows about but no schedule governs. It is backed up
                // by nothing, and saying so beats a screen of default values that would look like
                // settings somebody had chosen.
                Details = null;
                Failure = "No schedule on this machine governs this source, so there is nothing here to change. "
                    + "It is not being backed up on a schedule.";
                return;
            }

            Details = details;
            Apply(details.Settings);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Details = null;
            Failure = PlainFailure.Describe(error);
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>Writes the settings back, and reads them again to show what was actually kept.</summary>
    public async Task SaveAsync(CancellationToken cancellationToken)
    {
        if (Busy || Details is null) return;
        Busy = true;
        Failure = null;
        Saved = null;
        try
        {
            await _store.SaveAsync(_repositoryId, Current(), cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Failure = PlainFailure.Describe(error);
            return;
        }
        finally
        {
            Busy = false;
        }

        // Read back rather than assumed. The store may refuse part of what was asked, and a screen
        // that showed the request instead of the result would be describing a schedule that does not
        // exist on disk.
        await LoadAsync(cancellationToken);
        if (Failure is null)
        {
            Saved = "Saved.";
        }
    }

    /// <summary>
    /// Stops protecting this source. The backups already taken are not touched.
    /// </summary>
    public async Task RemoveAsync(CancellationToken cancellationToken)
    {
        if (Busy || Details is null) return;
        Busy = true;
        Failure = null;
        try
        {
            await _store.RemoveAsync(_repositoryId, cancellationToken);
            Removed = true;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Failure = PlainFailure.Describe(error);
        }
        finally
        {
            Busy = false;
        }
    }

    private SourceSettings Current() => new(
        Enabled,
        BackupHour,
        BackupMinute,
        DrillEveryDays,
        KeepDaily,
        KeepWeekly,
        KeepMonthly,
        Prune);

    private void Apply(SourceSettings settings)
    {
        Enabled = settings.Enabled;
        BackupHour = settings.BackupHour;
        BackupMinute = settings.BackupMinute;
        DrillEveryDays = settings.DrillEveryDays;
        KeepDaily = settings.KeepDaily;
        KeepWeekly = settings.KeepWeekly;
        KeepMonthly = settings.KeepMonthly;
        Prune = settings.Prune;
        OnPropertyChanged(nameof(RetentionEnabled));
    }

    /// <summary>A count that keeps nothing is not a count; it is the off switch, said badly.</summary>
    private static int? Positive(int? value) => value is { } number && number > 0 ? number : null;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? property = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        OnPropertyChanged(property);
    }

    private void OnPropertyChanged([CallerMemberName] string? property = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
