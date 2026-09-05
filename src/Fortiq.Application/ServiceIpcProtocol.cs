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
}
