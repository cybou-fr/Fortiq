using Fortiq.Application;
using Fortiq.Domain;

namespace Fortiq.Scheduling;

/// <summary>Performs the backup a schedule asks for. Composed elsewhere, because it needs the kit,
/// the engine and the credentials that this assembly deliberately knows nothing about.</summary>
public interface IScheduledBackup
{
    Task<BackupReceipt> RunAsync(BackupSchedule schedule, CancellationToken cancellationToken);
}

/// <summary>What happened to one schedule in one pass.</summary>
public sealed record ScheduleRunOutcome(
    string ScheduleId,
    DueVerdict Verdict,
    string? SnapshotId = null,
    string? Failure = null);

/// <summary>
/// Runs the schedules that are due, one pass at a time. Which are due is decided by
/// <see cref="ScheduleDecision"/>; this is what turns that decision into work and records what came
/// of it.
/// </summary>
/// <remarks>
/// A schedule that fails does not stop the others, and its failure is written to its own state
/// rather than raised: a machine with five schedules should not lose four of them because the first
/// repository was unreachable. The attempt is recorded whatever the outcome, so a failing schedule
/// waits for its next occurrence instead of retrying in a tight loop.
/// </remarks>
public sealed class ScheduledBackupRunner
{
    private readonly IScheduleStore _store;
    private readonly IScheduledBackup _backup;
    private readonly TimeProvider _clock;

    public ScheduledBackupRunner(IScheduleStore store, IScheduledBackup backup, TimeProvider? clock = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _backup = backup ?? throw new ArgumentNullException(nameof(backup));
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<IReadOnlyList<ScheduleRunOutcome>> RunDueAsync(CancellationToken cancellationToken)
    {
        var outcomes = new List<ScheduleRunOutcome>();
        var schedules = await _store.ReadSchedulesAsync(cancellationToken);
        if (_store is IScheduleIssueSource issueSource)
        {
            outcomes.AddRange(issueSource.LastReadIssues.Select(issue => new ScheduleRunOutcome(
                $"invalid:{issue.FileName}",
                DueVerdict.Disabled,
                Failure: issue.Failure)));
        }

        foreach (var schedule in schedules)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ScheduleState state;
            try
            {
                state = await _store.ReadStateAsync(schedule.Id, cancellationToken);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // A state file damaged by a power cut is one schedule's history, not every
                // schedule's. Isolating the schedule file but not its state would have left one
                // truncated JSON able to stop every other backup on the machine.
                outcomes.Add(new ScheduleRunOutcome(
                    schedule.Id,
                    DueVerdict.Disabled,
                    Failure: $"The recorded history for this schedule could not be read: {error.Message}"));
                continue;
            }

            var decision = ScheduleDecision.Evaluate(schedule, state, _clock.GetUtcNow());
            if (decision.Verdict != DueVerdict.Due)
            {
                outcomes.Add(new ScheduleRunOutcome(schedule.Id, decision.Verdict));
                continue;
            }

            outcomes.Add(await RunOneAsync(schedule, state, cancellationToken));
        }

        return outcomes;
    }

    /// <summary>
    /// Runs one schedule now, because somebody asked for it, whether or not it is due.
    /// </summary>
    /// <remarks>
    /// A person asking for a backup is not the same event as a clock reaching 02:30, but what happens
    /// afterwards must be: the attempt is recorded in the same state, so the schedule's own history is
    /// one history rather than two, and a manual run counts as the occurrence a later pass would
    /// otherwise repeat.
    ///
    /// A paused schedule still runs when it is asked to. Pausing is how somebody says "not on your
    /// own", which is a statement about the clock and not about them: refusing here would mean a
    /// person who had turned the nightly run off could no longer back the folder up at all, and the
    /// button that proves recovery already works on a paused source.
    /// </remarks>
    public async Task<ScheduleRunOutcome> RunNowAsync(string scheduleId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scheduleId);

        var schedules = await _store.ReadSchedulesAsync(cancellationToken);
        var schedule = schedules.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, scheduleId, StringComparison.OrdinalIgnoreCase));

        if (schedule is null)
        {
            return new ScheduleRunOutcome(
                scheduleId,
                DueVerdict.Disabled,
                Failure: $"No schedule on this machine has the id '{scheduleId}', so there is nothing to back up.");
        }

        ScheduleState state;
        try
        {
            state = await _store.ReadStateAsync(schedule.Id, cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return new ScheduleRunOutcome(
                schedule.Id,
                DueVerdict.Disabled,
                Failure: $"The recorded history for this schedule could not be read: {error.Message}");
        }

        return await RunOneAsync(schedule, state, cancellationToken);
    }

    private async Task<ScheduleRunOutcome> RunOneAsync(
        BackupSchedule schedule,
        ScheduleState state,
        CancellationToken cancellationToken)
    {
        var startedAt = _clock.GetUtcNow();
        try
        {
            var receipt = await _backup.RunAsync(schedule, cancellationToken);
            await _store.WriteStateAsync(
                state with
                {
                    LastAttemptAt = startedAt,
                    LastSuccessAt = _clock.GetUtcNow(),
                    LastSnapshotId = receipt.SnapshotId,
                    LastFailure = null
                },
                cancellationToken);

            return new ScheduleRunOutcome(schedule.Id, DueVerdict.Due, receipt.SnapshotId);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            // The attempt is recorded even though it failed, so the schedule moves on instead of
            // being due again immediately, and the reason survives for whoever looks later.
            await _store.WriteStateAsync(
                state with { LastAttemptAt = startedAt, LastFailure = failure.Message },
                CancellationToken.None);

            return new ScheduleRunOutcome(schedule.Id, DueVerdict.Due, null, failure.Message);
        }
    }
}
