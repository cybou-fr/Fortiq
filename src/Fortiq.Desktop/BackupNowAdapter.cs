using System.Runtime.Versioning;
using Fortiq.Desktop.ViewModels;
using Fortiq.Monitoring;
using Fortiq.Operations;
using Fortiq.Scheduling;

namespace Fortiq.Desktop;

/// <summary>
/// Backs a source up now, because somebody asked rather than because a clock reached 02:30.
/// </summary>
/// <remarks>
/// Until this existed there was no way to take a backup from the application at all. On an installed
/// machine that meant waiting for the nightly occurrence; in portable mode, and on a machine whose
/// device key could not be created, it meant a repository that was provisioned and then never written
/// to again. A backup product whose backups cannot be started by the person who wants one is not
/// finished, however good its evidence is.
///
/// Both modes run the same operation the scheduler runs, through the same runner, and record the
/// result in the same schedule state. Installed mode hands it to the service for the reason the other
/// privileged operations are handed over - the machine-scoped key and the receipt directory live on
/// that side of the boundary - and, as with provisioning, there is deliberately no fallback that does
/// the work here when the service is down.
/// </remarks>
public sealed class BackupNowAdapter : IBackupNow
{
    private readonly FileSystemScheduleStore _schedules;
    private readonly ScheduledBackupRunner _runner;
    private readonly HealthPublisher _health;
    private readonly IServiceIpcClient? _serviceClient;

    public BackupNowAdapter(
        FileSystemScheduleStore schedules,
        ScheduledBackupRunner runner,
        HealthPublisher health,
        IServiceIpcClient? serviceClient = null)
    {
        _schedules = schedules ?? throw new ArgumentNullException(nameof(schedules));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _health = health ?? throw new ArgumentNullException(nameof(health));
        _serviceClient = serviceClient;
    }

    [SupportedOSPlatform("windows")]
    public async Task<BackupNowResult> BackupAsync(string repositoryId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryId);

        if (_serviceClient is not null)
        {
            if (!await _serviceClient.IsServiceAvailableAsync(cancellationToken))
            {
                throw new InvalidOperationException(
                    "The Fortiq service is unavailable, so this backup cannot be started. Start the Fortiq service and try again.");
            }

            var response = await _serviceClient.BackupAsync(repositoryId, cancellationToken);
            return new BackupNowResult(response.Success, response.SnapshotId, response.ErrorMessage);
        }

        var schedule = await ScheduleLookup.FindForRepositoryAsync(_schedules, repositoryId, cancellationToken)
            ?? throw new InvalidOperationException(
                "Nothing on this machine says where that repository is, so there is nothing to back up into.");

        try
        {
            var outcome = await _runner.RunNowAsync(schedule.Id, cancellationToken);
            return new BackupNowResult(
                outcome.Failure is null && outcome.SnapshotId is not null,
                outcome.SnapshotId,
                outcome.Failure);
        }
        finally
        {
            // The screen that asked is the screen that has to show what came of it, and the report is
            // what it reads. Republished even after a failure, for the same reason the drill does it:
            // a report left describing the state from before the attempt is worse than no report.
            await _health.PublishAsync(cancellationToken);
        }
    }
}
