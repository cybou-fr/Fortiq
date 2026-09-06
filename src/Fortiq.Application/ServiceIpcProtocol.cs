namespace Fortiq.Application;

/// <summary>
/// Protocol messages exchanged across the local Named Pipe between interactive Fortiq.Desktop
/// and the privileged Fortiq background Windows Service (ADR-007, Spec 15).
/// </summary>
public static class ServiceIpcProtocol
{
    public const string PipeName = "Fortiq.Service.Ipc";

    public sealed record Request(string Command, string? PayloadJson = null);
    public sealed record Response(bool Success, string? ErrorMessage = null, string? PayloadJson = null);

    public sealed record ProvisionPayload(string RepositoryLocation, string KitDirectory, string SourcePath);
    public sealed record ProvisionResponse(string RepositoryId, string Mnemonic, bool DeviceUnlockAvailable, bool BackupScheduled, string? SchedulingFailure = null);

    public sealed record ProvePayload(string RepositoryId);
    public sealed record ProveResponse(bool Success, string? ErrorMessage = null);

    public sealed record BackupPayload(string RepositoryId);

    /// <summary>What one on-demand backup did.</summary>
    /// <param name="Success">Whether a snapshot was written.</param>
    /// <param name="SnapshotId">The snapshot, when there is one. Absent on failure.</param>
    /// <param name="ErrorMessage">
    /// Why it did not run, in the words the person sees. A failed backup answers here rather than by
    /// failing the request: the service did what it was asked, and the answer is that the backup did
    /// not work - which is a different thing from the service refusing to try.
    /// </param>
    public sealed record BackupResponse(bool Success, string? SnapshotId = null, string? ErrorMessage = null);

    /// <summary>
    /// The settings of one schedule, as the application edits them.
    /// </summary>
    /// <remarks>
    /// A flat shape rather than the domain record: this crosses a process boundary between two
    /// versions that may not be the same build, so it says what it means in the plainest terms it can
    /// - minutes since midnight, whole days, counts - and the service turns them back into the domain
    /// on its own side. Nulls mean off: no drills, no retention.
    /// </remarks>
    public sealed record SchedulePreferencesPayload(
        string RepositoryId,
        bool Enabled,
        int BackupMinuteOfDay,
        int? DrillEveryDays,
        int? KeepDaily,
        int? KeepWeekly,
        int? KeepMonthly,
        bool Prune);

    public sealed record RemoveSchedulePayload(string RepositoryId);

    /// <summary>Asks for the lock an interrupted run left in a repository to be cleared.</summary>
    public sealed record ClearLockPayload(string RepositoryId);
}
