using System.Runtime.Versioning;
using Fortiq.Scheduling;

namespace Fortiq.Operations;

/// <summary>
/// Runs a scheduled restore drill with nobody present, by restoring from the repository and looking
/// at what came out.
/// </summary>
/// <remarks>
/// Thin on purpose: <see cref="ProvenRestore"/> already does the work and writes the receipt that
/// makes the proof durable. What this adds is the shape the scheduler needs, and one decision -
/// unlocking uses the device envelope, as all unattended work does. A drill that prompted for the
/// recovery phrase would be a drill that never runs.
/// </remarks>
public sealed class UnattendedRestoreDrill : IScheduledDrill
{
    private readonly ProvenRestore _restore;

    public UnattendedRestoreDrill(ProvenRestore restore) =>
        _restore = restore ?? throw new ArgumentNullException(nameof(restore));

    [SupportedOSPlatform("windows")]
    public async Task<DrillResult> RunAsync(BackupSchedule schedule, CancellationToken cancellationToken)
    {
        var proof = await _restore.ProveAsync(schedule, cancellationToken);
        return new DrillResult(proof.SnapshotId, proof.FilesOnDisk, proof.BytesRestored);
    }
}
