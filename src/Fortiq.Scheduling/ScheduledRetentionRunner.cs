using Fortiq.Domain;

namespace Fortiq.Scheduling;

/// <summary>
/// Applies a repository's retention policy. Composed elsewhere, because it needs the kit, the engine
/// and the credentials this assembly deliberately knows nothing about.
/// </summary>
public interface IScheduledRetention
{
    Task<RetentionReceipt> RunAsync(BackupSchedule schedule, CancellationToken cancellationToken);
}

/// <summary>What happened to one repository's retention in one pass.</summary>
public sealed record RetentionRunOutcome(
    string ScheduleId,
    DueVerdict Verdict,
    int Removed = 0,
    bool Pruned = false,
    string? Failure = null);

/// <summary>
/// Runs the retention policies that are due.
/// </summary>
/// <remarks>
/// This is the only scheduled operation that destroys anything, and the code is arranged around that
/// asymmetry. A backup that does not run leaves yesterday's backup; a drill that does not run leaves
/// recovery unproven; a retention run that goes wrong removes history that cannot be brought back.
/// <para>
/// So: retention is opt-in and requires an explicit policy, silence is read as "keep everything",
/// missed occurrences are never caught up, and a repository that is busy is left alone rather than
/// waited for. The engine's own refusal to leave a source with no snapshots remains the last line,
/// and none of this replaces it.
/// </para>
/// </remarks>
public sealed class ScheduledRetentionRunner
{
    private readonly IScheduleStore _store;
    private readonly IScheduledRetention _retention;
    private readonly TimeProvider _clock;

    public ScheduledRetentionRunner(IScheduleStore store, IScheduledRetention retention, TimeProvider? clock = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _retention = retention ?? throw new ArgumentNullException(nameof(retention));
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<IReadOnlyList<RetentionRunOutcome>> RunDueAsync(CancellationToken cancellationToken)
    {
        var outcomes = new List<RetentionRunOutcome>();
        foreach (var schedule in await _store.ReadSchedulesAsync(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            ScheduleState state;
            try
            {
                state = await _store.ReadStateAsync(schedule.RetentionStateId, cancellationToken);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                outcomes.Add(new RetentionRunOutcome(
                    schedule.Id,
                    DueVerdict.Disabled,
                    Failure: $"The recorded retention history for this schedule could not be read: {error.Message}"));
                continue;
            }

            var decision = ScheduleDecision.EvaluateRetention(schedule, state, _clock.GetUtcNow());
            if (decision.Verdict != DueVerdict.Due)
            {
                outcomes.Add(new RetentionRunOutcome(schedule.Id, decision.Verdict));
                continue;
            }

            outcomes.Add(await RunOneAsync(schedule, state, cancellationToken));
        }

        return outcomes;
    }

    private async Task<RetentionRunOutcome> RunOneAsync(
        BackupSchedule schedule,
        ScheduleState state,
        CancellationToken cancellationToken)
    {
        var startedAt = _clock.GetUtcNow();
        try
        {
            var receipt = await _retention.RunAsync(schedule, cancellationToken);
            await _store.WriteStateAsync(
                state with
                {
                    LastAttemptAt = startedAt,
                    LastSuccessAt = _clock.GetUtcNow(),
                    LastFailure = null
                },
                cancellationToken);

            return new RetentionRunOutcome(schedule.Id, DueVerdict.Due, receipt.RemovedSnapshotIds.Count, receipt.Pruned);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            // Recorded as an attempt, so a repository that is busy or unhappy is not asked to delete
            // snapshots again on the next tick. Retention is the one operation where retrying
            // eagerly is worse than waiting: everything it does is irreversible.
            await _store.WriteStateAsync(
                state with { LastAttemptAt = startedAt, LastFailure = failure.Message },
                CancellationToken.None);

            return new RetentionRunOutcome(schedule.Id, DueVerdict.Due, Failure: failure.Message);
        }
    }
}
