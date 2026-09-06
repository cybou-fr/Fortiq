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
    private readonly TimeProvider _clock;
    public static TimeSpan ReportMaxAge { get; } = TimeSpan.FromMinutes(5);

    private string? _failure;
    private bool _busy;
    private DateTimeOffset? _reportProducedAt;
    private HealthStoreState _state = HealthStoreState.NotInitialized;

    public RepositoriesViewModel(IHealthSource health, IProveRecovery prove, TimeProvider? clock = null)
    {
        _health = health ?? throw new ArgumentNullException(nameof(health));
        _prove = prove ?? throw new ArgumentNullException(nameof(prove));
        _clock = clock ?? TimeProvider.System;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<RepositoryRowViewModel> Repositories { get; } = [];

    public string? Failure { get => _failure; private set => Set(ref _failure, value); }

    public bool Busy { get => _busy; private set => Set(ref _busy, value); }

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

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (Busy) return;
        Busy = true;
        Failure = null;
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
            Failure = stale ? "The health report is older than five minutes or its timestamp is in the future. Current protection is unknown."
                : result.State == HealthStoreState.Corrupt ? result.Detail : null;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // A health screen that hides its own failure is the worst kind: it looks calm.
            Failure = PlainFailure.Describe(error);
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
        Failure = null;
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
            Failure = outcome;
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
