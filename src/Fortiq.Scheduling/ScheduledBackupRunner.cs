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

            var state = await _store.ReadStateAsync(schedule.Id, cancellationToken);
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
