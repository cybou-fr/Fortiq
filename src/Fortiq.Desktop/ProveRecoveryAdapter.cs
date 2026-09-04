using System.Runtime.Versioning;
using Fortiq.Desktop.ViewModels;
using Fortiq.Infrastructure.Keys;
using Fortiq.Operations;
using Fortiq.Scheduling;

namespace Fortiq.Desktop;

/// <summary>
/// The button that turns "backed up" into "known to come back". It restores the newest snapshot to a
/// scratch directory, checks what came out, writes a receipt, and republishes the health report.
/// </summary>
/// <remarks>
/// The report is republished here on purpose. The receipt is what makes the repository proven, but
/// the screen reads the published report, and a proof that would not show until the service's next
/// pass would leave someone pressing the button again believing it had done nothing.
/// </remarks>
public sealed class ProveRecoveryAdapter : IProveRecovery
{
    private readonly FileSystemScheduleStore _schedules;
    private readonly ProvenRestore _restore;
    private readonly HealthPublisher _health;

    public ProveRecoveryAdapter(
        FileSystemScheduleStore schedules,
        ProvenRestore restore,
        HealthPublisher health)
    {
        _schedules = schedules ?? throw new ArgumentNullException(nameof(schedules));
        _restore = restore ?? throw new ArgumentNullException(nameof(restore));
        _health = health ?? throw new ArgumentNullException(nameof(health));
    }

    [SupportedOSPlatform("windows")]
    public async Task<bool> ProveAsync(string repositoryId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryId);

        var schedule = await FindAsync(repositoryId, cancellationToken)
            ?? throw new InvalidOperationException(
                "Nothing on this machine says where that repository is, so there is nothing to restore from.");

        try
        {
            await _restore.ProveAsync(schedule, cancellationToken);
            return true;
        }
        catch (RestoreProofFailedException)
        {
            // A restore that ran and produced the wrong thing is the answer to the question the
            // button asked; it is reported as "no", not as an error in Fortiq.
            return false;
        }
        finally
        {
            // Whatever happened, the report is rebuilt from the receipts, so the screen shows the
            // state that now exists rather than the one from before the attempt.
            await _health.PublishAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Finds the schedule for a repository. The screen knows the repository by the identity in its
    /// kit, which is the identity the repository states about itself - not by the path, which two
    /// schedules could share and which can change without the repository changing.
    /// </summary>
    private async Task<BackupSchedule?> FindAsync(string repositoryId, CancellationToken cancellationToken)
    {
        BackupSchedule? byId = null;
        foreach (var schedule in await _schedules.ReadSchedulesAsync(cancellationToken))
        {
            if (string.Equals(schedule.Id, repositoryId, StringComparison.OrdinalIgnoreCase))
            {
                byId = schedule;
            }

            try
            {
                var kit = await RecoveryKitStore.ReadAsync(schedule.KitDirectory, cancellationToken);
                if (string.Equals(kit.Manifest.RepositoryId, repositoryId, StringComparison.OrdinalIgnoreCase))
                {
                    return schedule;
                }
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                // A kit that cannot be read cannot identify its repository. The report already says
                // that repository is at risk; skipping it here keeps one bad kit from hiding the rest.
            }
        }

        // The health report falls back to the schedule ID when there is no readable kit, so the
        // button has to be able to follow it back the same way.
        return byId;
    }
}
