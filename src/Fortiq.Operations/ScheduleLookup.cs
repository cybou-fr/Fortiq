using Fortiq.Infrastructure.Keys;
using Fortiq.Scheduling;

namespace Fortiq.Operations;

/// <summary>
/// Finds the schedule that governs a repository, given the identity the screens use.
/// </summary>
/// <remarks>
/// The desktop and the health report know a repository by the identity stated in its kit; a schedule
/// is keyed by its own id, and the two coincide only when provisioning wrote the schedule. Every
/// action that starts from a repository on screen and has to reach a schedule needs this translation,
/// which is why it is one function rather than a copy per caller: three copies had already drifted
/// into three slightly different answers to the same question.
/// </remarks>
public static class ScheduleLookup
{
    /// <summary>The schedule for <paramref name="repositoryId"/>, or null when nothing on this machine has it.</summary>
    public static async Task<BackupSchedule?> FindForRepositoryAsync(
        IScheduleStore schedules,
        string repositoryId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(schedules);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryId);

        BackupSchedule? byId = null;
        foreach (var schedule in await schedules.ReadSchedulesAsync(cancellationToken))
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
                // A kit that cannot be read cannot identify its repository. The health report already
                // reports that repository, and skipping it here keeps one unreadable kit from hiding
                // every schedule behind it.
            }
        }

        // The health report falls back to the schedule id when no kit could be read, so a caller
        // following that identity back has to be able to land on the same schedule.
        return byId;
    }
}
