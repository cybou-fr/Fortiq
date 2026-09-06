using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Fortiq.Monitoring;

namespace Fortiq.Desktop.ViewModels;

/// <summary>Reads the health report the service publishes. The desktop asks the files, not the service.</summary>
public interface IHealthSource
{
    Task<HealthReadResult> ReadAsync(CancellationToken cancellationToken);
}

public enum HealthStoreState
{
    NotInitialized,
    Empty,
    Active,
    Corrupt,
    Stale
}

public sealed record HealthReadResult(HealthStoreState State, HealthReport? Report = null, string? Detail = null);

/// <summary>
/// Proves that a repository can be restored, by restoring from it. This is the action that turns a
/// repository from "backed up" into "known to come back", and nothing else can.
/// </summary>
public interface IProveRecovery
{
    Task<bool> ProveAsync(string repositoryId, CancellationToken cancellationToken);
}

/// <summary>What one on-demand backup did.</summary>
/// <param name="Success">Whether a snapshot was written.</param>
/// <param name="SnapshotId">The snapshot, when there is one.</param>
/// <param name="Failure">Why it did not work, in words the person can act on.</param>
public sealed record BackupNowResult(bool Success, string? SnapshotId = null, string? Failure = null);

/// <summary>
/// Backs a repository up now, at somebody's request. Separate from the schedule that runs unattended,
/// and deliberately not a second way of doing it: both end in the same run, the same receipt and the
/// same state.
/// </summary>
public interface IBackupNow
{
    Task<BackupNowResult> BackupAsync(string repositoryId, CancellationToken cancellationToken);
}

/// <summary>One repository as the person sees it.</summary>
public sealed class RepositoryRowViewModel
{
    public RepositoryRowViewModel(RepositoryHealth health)
    {
        ArgumentNullException.ThrowIfNull(health);
        Health = health;
    }

    public RepositoryHealth Health { get; }

    public string Title => Health.ScheduleId ?? Health.RepositoryId;

    /// <summary>
    /// What Fortiq is willing to say, in the words a person needs. "Backed up" is never offered on
    /// its own: it is the claim that misleads.
    /// </summary>
    public string Summary => Health.Findings.Any(finding => finding.Code == "report-stale")
        ? "Current protection is unknown: the health report is out of date."
        : Health.Verdict switch
    {
        HealthVerdict.Recoverable => "Recoverable: checked and restored recently.",
        HealthVerdict.Unproven => "Backed up, but recovery has not been proven.",
        _ => "At risk: this may not be recoverable today."
    };

    public string Detail => Health.Findings.Count == 0
        ? "Nothing outstanding."
        : string.Join(Environment.NewLine, Health.Findings.Select(finding => finding.Detail));

    /// <summary>Only a repository that exists and is not already proven has anything to prove.</summary>
    public bool CanProveRecovery => Health.Facts.LastBackupAt is not null;
}

/// <summary>The main screen: what exists, whether it can be recovered, and what to do about it.</summary>
public sealed class RepositoriesViewModel : INotifyPropertyChanged
{
    private readonly IHealthSource _health;
    private readonly IProveRecovery _prove;
    private readonly IBackupNow? _backup;
    private readonly TimeProvider _clock;
    public static TimeSpan ReportMaxAge { get; } = TimeSpan.FromMinutes(5);

    private string? _failure;
    private bool _failureIsSticky;
    private string? _activity;
    private CancellationTokenSource? _running;
    private bool _busy;
    private DateTimeOffset? _reportProducedAt;
    private HealthStoreState _state = HealthStoreState.NotInitialized;

    public RepositoriesViewModel(
        IHealthSource health,
        IProveRecovery prove,
        TimeProvider? clock = null,
        IBackupNow? backup = null)
    {
        _health = health ?? throw new ArgumentNullException(nameof(health));
        _prove = prove ?? throw new ArgumentNullException(nameof(prove));
        _backup = backup;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Whether this machine can start a backup at all. False leaves the button off the screen.</summary>
    public bool CanBackupNow => _backup is not null;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<RepositoryRowViewModel> Repositories { get; } = [];

    public string? Failure { get => _failure; private set => Set(ref _failure, value); }

    public bool Busy { get => _busy; private set => Set(ref _busy, value); }

    /// <summary>Whether what is running can be stopped by asking.</summary>
    public bool CanCancel => _running is { IsCancellationRequested: false };

    /// <summary>
    /// Asks the running operation to stop.
    /// </summary>
    /// <remarks>
    /// The work carries on until the engine notices, which is why the screen says the request was made
    /// rather than reporting it as already done - and why a cancelled backup can leave its lock in the
    /// repository, which the source's own screen offers to clear.
    /// </remarks>
    public void CancelRunning()
    {
        if (_running is { IsCancellationRequested: false } running)
        {
            Activity = "Stopping…";
            running.Cancel();
            OnPropertyChanged(nameof(CanCancel));
        }
    }

    /// <summary>What is running right now, named. Null when nothing is.</summary>
    /// <remarks>
    /// "Busy" alone disables every button and explains nothing; on an operation that can take minutes,
    /// a screen that has gone quiet and grey is indistinguishable from one that has hung.
    /// </remarks>
    public string? Activity { get => _activity; private set => Set(ref _activity, value); }

    public DateTimeOffset? ReportProducedAt { get => _reportProducedAt; private set => Set(ref _reportProducedAt, value); }

    public HealthStoreState State { get => _state; private set => Set(ref _state, value); }

    /// <summary>
    /// The one line to show at the top. When there is no report at all it says so rather than showing
    /// an empty list, which would read as "nothing is wrong".
    /// </summary>
    public string Headline => State switch
    {
        HealthStoreState.NotInitialized => "Protect what matters before you need it.",
        HealthStoreState.Empty => "No protected sources yet.",
        HealthStoreState.Corrupt => "Protection status is temporarily unavailable.",
        HealthStoreState.Stale => "Protection status is out of date. Refresh or check the Fortiq service.",
        _ => Repositories.Count == 0
            ? "No protected sources yet."
            : Repositories.Any(row => row.Health.Verdict == HealthVerdict.AtRisk)
                ? "Something may not be recoverable today."
                : Repositories.Any(row => row.Health.Verdict == HealthVerdict.Unproven)
                    ? "Everything is backed up; recovery has not been proven for all of it."
                    : "Your data is recoverable."
    };

    /// <summary>
    /// Reports a failure to read something the screen was showing, such as the receipt history.
    /// </summary>
    /// <remarks>
    /// Sticky like an operation's failure, and for the same reason: a screen that could not read the
    /// history has not become able to read it thirty seconds later, and a message that leaves on its
    /// own turns an unreadable directory into an empty one.
    /// </remarks>
    public void ReportReadFailure(string failure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failure);
        ReportOperationFailure(failure);
    }

    /// <summary>Puts the last failure away, because the person has read it.</summary>
    public void ClearFailure()
    {
        _failureIsSticky = false;
        Failure = null;
    }

    /// <summary>
    /// Records why an operation the person started did not work, and keeps it there until they put it
    /// away or start another one.
    /// </summary>
    /// <remarks>
    /// The screen polls every thirty seconds, and a refresh clears the failure it found last time -
    /// which is right for a failure about reading the report, and wrong for a failure about a backup
    /// somebody watched fail: within half a minute the message would leave the screen on its own,
    /// while the state that caused it had not changed.
    /// </remarks>
    private void ReportOperationFailure(string failure)
    {
        Failure = failure;
        _failureIsSticky = true;
    }

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (Busy) return;
        Busy = true;
        if (!_failureIsSticky)
        {
            Failure = null;
        }

        try
        {
            var result = await _health.ReadAsync(cancellationToken);
            var age = result.Report is { } report ? _clock.GetUtcNow() - report.ProducedAt : TimeSpan.Zero;
            var stale = result.Report is not null && (age > ReportMaxAge || age < -TimeSpan.FromMinutes(1));
            State = stale ? HealthStoreState.Stale : result.State;
            Repositories.Clear();
            foreach (var repository in result.Report?.Repositories ?? [])
            {
                Repositories.Add(new RepositoryRowViewModel(stale ? repository with
                {
                    Verdict = repository.Verdict == HealthVerdict.AtRisk ? HealthVerdict.AtRisk : HealthVerdict.Unproven,
                    Findings = [.. repository.Findings, new HealthFinding("report-stale", "This report is out of date; current protection has not been verified.")]
                } : repository));
            }

            ReportProducedAt = result.Report?.ProducedAt;
            var readFailure = stale
                ? "The health report is older than five minutes or its timestamp is in the future. Current protection is unknown."
                : result.State == HealthStoreState.Corrupt ? result.Detail : null;
            if (readFailure is not null || !_failureIsSticky)
            {
                Failure = readFailure;
                _failureIsSticky = false;
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // A health screen that hides its own failure is the worst kind: it looks calm.
            Failure = PlainFailure.Describe(error);
            _failureIsSticky = false;
            State = HealthStoreState.Corrupt;
            Repositories.Clear();
            ReportProducedAt = null;
        }
        finally
        {
            Busy = false;
            OnPropertyChanged(nameof(Headline));
        }
    }

    public async Task ProveRecoveryAsync(RepositoryRowViewModel repository, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repository);
        if (Busy || !repository.CanProveRecovery)
        {
            return;
        }

        Busy = true;
        ClearFailure();
        string? outcome = null;
        try
        {
            if (!await _prove.ProveAsync(repository.Health.RepositoryId, cancellationToken))
            {
                outcome = "The restore did not produce what the snapshot says it should.";
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            outcome = PlainFailure.Describe(error);
        }
        finally
        {
            Busy = false;
        }

        // The refresh comes first so the screen shows the new state, and the outcome is put back
        // afterwards: a failed proof that vanished on refresh would leave someone believing it worked.
        await RefreshAsync(cancellationToken);
        if (outcome is not null)
        {
            ReportOperationFailure(outcome);
        }
    }

    /// <summary>Backs one repository up now and shows what happened.</summary>
    /// <remarks>
    /// A backup that failed is reported as a failure and not as a silent refresh. The order is the one
    /// the recovery proof uses and for the same reason: refresh first so the screen shows the state
    /// that now exists, then put the outcome back, because an outcome cleared by the refresh would
    /// leave somebody believing a backup worked when it did not.
    /// </remarks>
    public async Task BackupNowAsync(RepositoryRowViewModel repository, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repository);
        if (Busy || _backup is null)
        {
            return;
        }

        Busy = true;
        ClearFailure();
        Activity = $"Backing up {repository.Title}…";
        using var running = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _running = running;
        OnPropertyChanged(nameof(CanCancel));
        string? outcome = null;
        try
        {
            var result = await _backup.BackupAsync(repository.Health.RepositoryId, running.Token);
            if (!result.Success)
            {
                outcome = result.Failure ?? "The backup did not complete, and gave no reason.";
            }
        }
        catch (OperationCanceledException) when (running.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // Somebody stopped it, which is an outcome rather than a fault - but it is said out loud,
            // because a backup that was stopped is a backup that did not happen.
            outcome = "The backup was stopped before it finished, so nothing new was recorded. "
                + "If the next backup says the repository is locked, clear the lock from this source's settings.";
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            outcome = PlainFailure.Describe(error);
        }
        finally
        {
            _running = null;
            Busy = false;
            Activity = null;
            OnPropertyChanged(nameof(CanCancel));
        }

        await RefreshAsync(cancellationToken);
        if (outcome is not null)
        {
            ReportOperationFailure(outcome);
        }
    }

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
