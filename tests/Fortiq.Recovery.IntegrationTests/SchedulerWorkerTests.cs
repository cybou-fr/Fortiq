using Fortiq.Operations;
using Fortiq.Application;
using Fortiq.Domain;
using Fortiq.Scheduling;
using Fortiq.Service;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fortiq.Recovery.IntegrationTests;

/// <summary>
/// The loop that asks what is due. A service that stopped on the first failure would take every
/// other schedule down with the one that broke, and would back nothing up until someone noticed.
/// </summary>
public sealed class SchedulerWorkerTests
{
    [Fact]
    public async Task APassThatThrowsDoesNotEndTheService()
    {
        var worker = new SchedulerWorker(
            new ScheduledBackupRunner(new BrokenStore(), new UnusedBackup()),
            SchedulerOptions.Default,
            NullLogger<SchedulerWorker>.Instance);

        // Reading the schedules themselves can fail - a malformed file, an unreachable directory.
        await worker.RunOnePassAsync(CancellationToken.None);
    }

    [Fact]
    public async Task CancellationStopsTheLoopRatherThanBeingSwallowed()
    {
        var worker = new SchedulerWorker(
            new ScheduledBackupRunner(new BrokenStore(cancel: true), new UnusedBackup()),
            SchedulerOptions.Default,
            NullLogger<SchedulerWorker>.Instance);

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.RunOnePassAsync(cancellation.Token));
    }

    private sealed class BrokenStore(bool cancel = false) : IScheduleStore
    {
        public Task<IReadOnlyList<BackupSchedule>> ReadSchedulesAsync(CancellationToken cancellationToken)
        {
            if (cancel)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            throw new InvalidDataException("a schedule file is malformed");
        }

        public Task<ScheduleState> ReadStateAsync(string scheduleId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task WriteStateAsync(ScheduleState state, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class UnusedBackup : IScheduledBackup
    {
        public Task<BackupReceipt> RunAsync(BackupSchedule schedule, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
