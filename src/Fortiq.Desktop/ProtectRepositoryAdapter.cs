using System.Globalization;
using Fortiq.Application;
using Fortiq.Desktop.ViewModels;
using Fortiq.Provisioning;
using Fortiq.Scheduling;

namespace Fortiq.Desktop;

/// <summary>
/// Turns the wizard's three answers into a repository, a recovery kit and, when possible, a nightly schedule.
/// In installed mode with the Fortiq Service running, provisions via Service IPC to bind to the machine TPM
/// key and isolate %ProgramData%\Fortiq\work from standard user writes.
/// </summary>
public sealed class ProtectRepositoryAdapter : IProtectRepository
{
    private readonly RepositoryProvisioner _provisioner;
    private readonly FortiqStatePaths _paths;
    private readonly TimeOnly _nightly;
    private readonly IServiceIpcClient? _serviceClient;

    public ProtectRepositoryAdapter(
        RepositoryProvisioner provisioner,
        FortiqStatePaths paths,
        TimeOnly? nightly = null,
        IServiceIpcClient? serviceClient = null)
    {
        _provisioner = provisioner ?? throw new ArgumentNullException(nameof(provisioner));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _nightly = nightly ?? new TimeOnly(2, 30);
        _serviceClient = serviceClient;
    }

    public async Task<ProtectedRepositoryResult> CreateAsync(
        ProtectRepositoryRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (_serviceClient is not null && await _serviceClient.IsServiceAvailableAsync(cancellationToken))
        {
            var ipcResponse = await _serviceClient.ProvisionAsync(
                request.RepositoryLocation,
                request.KitDirectory,
                request.SourcePath,
                cancellationToken);

            return new ProtectedRepositoryResult(
                ipcResponse.RepositoryId,
                ipcResponse.Mnemonic,
                ipcResponse.DeviceUnlockAvailable,
                ipcResponse.BackupScheduled,
                ipcResponse.SchedulingFailure);
        }

        var working = _paths.Working;
        Directory.CreateDirectory(working);

        var provisioned = await _provisioner.CreateAsync(
            request.RepositoryLocation,
            request.KitDirectory,
            working,
            cancellationToken);

        var id = provisioned.Repository.Id.ToString();
        try
        {
            await WriteScheduleAsync(Path.Combine(_paths.Schedules, "schedules"), id, request, _nightly, cancellationToken);
        }
        catch (Exception error)
        {
            return new ProtectedRepositoryResult(
                id,
                provisioned.RecoveryMnemonic,
                provisioned.DeviceUnlockAvailable,
                BackupScheduled: false,
                SchedulingFailure: $"The repository and recovery kit were created, but nightly backup scheduling failed: {error.Message}");
        }

        return new ProtectedRepositoryResult(id, provisioned.RecoveryMnemonic, provisioned.DeviceUnlockAvailable);
    }

    /// <summary>
    /// Writes the schedule file the service reads. Public and static so tests can write one and read
    /// it back through <c>FileSystemScheduleStore</c>.
    /// </summary>
    public static Task WriteScheduleAsync(
        string directory,
        string repositoryId,
        ProtectRepositoryRequest request,
        TimeOnly nightly,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(request);

        return FileSystemScheduleStore.WriteDefaultScheduleAsync(
            directory,
            repositoryId,
            request.RepositoryLocation,
            request.KitDirectory,
            request.SourcePath,
            nightly,
            cancellationToken);
    }
}