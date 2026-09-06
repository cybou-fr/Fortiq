using System.Runtime.Versioning;
using Fortiq.Desktop.ViewModels;
using Fortiq.Operations;
using Fortiq.Scheduling;

namespace Fortiq.Desktop;

/// <summary>
/// The button that turns "backed up" into "known to come back". It restores the newest snapshot to a
/// scratch directory, checks what came out, writes a receipt, and republishes the health report.
/// In installed mode with the Fortiq Service running, delegates the drill to the privileged service
/// via Service IPC so standard desktop users do not write to %ProgramData%\Fortiq\work\receipts directly.
/// </summary>
public sealed class ProveRecoveryAdapter : IProveRecovery
{
    private readonly FileSystemScheduleStore _schedules;
    private readonly ProvenRestore _restore;
    private readonly HealthPublisher _health;
    private readonly IServiceIpcClient? _serviceClient;

    public ProveRecoveryAdapter(
        FileSystemScheduleStore schedules,
        ProvenRestore restore,
        HealthPublisher health,
        IServiceIpcClient? serviceClient = null)
    {
        _schedules = schedules ?? throw new ArgumentNullException(nameof(schedules));
        _restore = restore ?? throw new ArgumentNullException(nameof(restore));
        _health = health ?? throw new ArgumentNullException(nameof(health));
        _serviceClient = serviceClient;
    }

    [SupportedOSPlatform("windows")]
    public async Task<bool> ProveAsync(string repositoryId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryId);

        if (_serviceClient is not null)
        {
            if (!await _serviceClient.IsServiceAvailableAsync(cancellationToken))
                throw new InvalidOperationException("The Fortiq service is unavailable. Start the service and retry the recovery proof.");
            return await _serviceClient.ProveRecoveryAsync(repositoryId, cancellationToken);
        }

        var schedule = await FindAsync(repositoryId, cancellationToken)
            ?? throw new InvalidOperationException(
                "Nothing on this machine says where that repository is, so there is nothing to restore from.");

        try
        {
            await _restore.ProveAsync(schedule, cancellationToken);
            return true;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // Keep preflight failures and evidence-write failures visible to the next service pass,
            // even when no receipt could be written. This shares the scheduled drill's state.
            var state = await _schedules.ReadStateAsync(schedule.DrillStateId, CancellationToken.None);
            await _schedules.WriteStateAsync(state with
            {
                LastAttemptAt = DateTimeOffset.UtcNow,
                LastFailure = error.Message
            }, CancellationToken.None);
            if (error is RestoreProofFailedException) return false;
            throw;
        }
        finally
        {
            // Whatever happened, the report is rebuilt from the receipts, so the screen shows the
            // state that now exists rather than the one from before the attempt.
            await _health.PublishAsync(cancellationToken);
        }
    }

    private Task<BackupSchedule?> FindAsync(string repositoryId, CancellationToken cancellationToken) =>
        ScheduleLookup.FindForRepositoryAsync(_schedules, repositoryId, cancellationToken);
}