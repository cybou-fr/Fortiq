namespace Fortiq.Scheduling;

/// <summary>
/// Proves that a repository can be restored. Composed elsewhere, because proving it needs the kit,
/// the engine and the credentials this assembly deliberately knows nothing about.
/// </summary>
public interface IScheduledDrill
{
    Task<DrillResult> RunAsync(BackupSchedule schedule, CancellationToken cancellationToken);
}

/// <summary>What a drill restored, in the terms the scheduler needs to record it.</summary>
public sealed record DrillResult(string SnapshotId, ulong FilesRestored, ulong BytesRestored);

/// <summary>What happened to one repository's drill in one pass.</summary>
public sealed record DrillRunOutcome(
    string ScheduleId,
    DueVerdict Verdict,
    string? SnapshotId = null,
    string? Failure = null);

/// <summary>
/// Runs the restore drills that are due. This is what makes a repository provably recoverable
/// without anyone remembering to check.
/// </summary>
/// <remarks>
/// The whole product rests on a distinction between a backup that ran and data that comes back, and
/// until this existed only a person clicking a button could establish the second one. A repository
/// on a machine nobody logs into would have stayed <c>Unproven</c> forever, which is honest but not
/// useful.
/// <para>
/// A drill is a full restore of the source into disposable space, so it is expensive in a way a
/// backup is not. Drills therefore never catch up on missed occurrences, they are opt-in per
/// schedule, and a failure is recorded against the drill's own state rather than the backup's - a
/// repository that cannot be restored today must not also stop being backed up.
/// </para>
/// </remarks>
public sealed class ScheduledDrillRunner
{
    private readonly IScheduleStore _store;
    private readonly IScheduledDrill _drill;
    private readonly TimeProvider _clock;

    public ScheduledDrillRunner(IScheduleStore store, IScheduledDrill drill, TimeProvider? clock = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _drill = drill ?? throw new ArgumentNullException(nameof(drill));
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<IReadOnlyList<DrillRunOutcome>> RunDueAsync(CancellationToken cancellationToken)
    {
        var outcomes = new List<DrillRunOutcome>();
        foreach (var schedule in await _store.ReadSchedulesAsync(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            ScheduleState state;
            try
            {
                state = await _store.ReadStateAsync(schedule.DrillStateId, cancellationToken);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                outcomes.Add(new DrillRunOutcome(
                    schedule.Id,
                    DueVerdict.Disabled,
                    Failure: $"The recorded drill history for this schedule could not be read: {error.Message}"));
                continue;
            }

            var decision = ScheduleDecision.EvaluateDrill(schedule, state, _clock.GetUtcNow());
            if (decision.Verdict != DueVerdict.Due)
            {
                outcomes.Add(new DrillRunOutcome(schedule.Id, decision.Verdict));
                continue;
            }

            outcomes.Add(await RunOneAsync(schedule, state, cancellationToken));
        }

        return outcomes;
    }

    private async Task<DrillRunOutcome> RunOneAsync(
        BackupSchedule schedule,
        ScheduleState state,
        CancellationToken cancellationToken)
    {
        var startedAt = _clock.GetUtcNow();
        try
        {
            var result = await _drill.RunAsync(schedule, cancellationToken);
            await _store.WriteStateAsync(
                state with
                {
                    LastAttemptAt = startedAt,
                    LastSuccessAt = _clock.GetUtcNow(),
                    LastSnapshotId = result.SnapshotId,
                    LastFailure = null
                },
                cancellationToken);

            return new DrillRunOutcome(schedule.Id, DueVerdict.Due, result.SnapshotId);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            // A drill that failed is the most important thing this machine knows, and it is recorded
            // as an attempt so the next pass does not immediately restore the whole source again
            // against a repository that has just demonstrated it cannot be read.
            await _store.WriteStateAsync(
                state with { LastAttemptAt = startedAt, LastFailure = failure.Message },
                CancellationToken.None);

            return new DrillRunOutcome(schedule.Id, DueVerdict.Due, null, failure.Message);
        }
    }
}
