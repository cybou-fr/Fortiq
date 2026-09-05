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
    private readonly Func<string, ObjectStorageCredentials, CancellationToken, Task>? _storeCredentials;

    public ProtectRepositoryAdapter(
        RepositoryProvisioner provisioner,
        FortiqStatePaths paths,
        TimeOnly? nightly = null,
        IServiceIpcClient? serviceClient = null,
        Func<string, ObjectStorageCredentials, CancellationToken, Task>? storeCredentials = null)
    {
        _provisioner = provisioner ?? throw new ArgumentNullException(nameof(provisioner));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _nightly = nightly ?? new TimeOnly(2, 30);
        _serviceClient = serviceClient;
        _storeCredentials = storeCredentials;
    }

    public async Task<ProtectedRepositoryResult> CreateAsync(
        ProtectRepositoryRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Written before anything is provisioned. The engine reaches the bucket during provisioning,
        // so credentials that arrive afterwards arrive too late - and they are stored per repository
        // rather than per machine, so a key issued for one bucket does not silently become the key
        // for every other.
        if (_storeCredentials is not null &&
            !string.IsNullOrWhiteSpace(request.StorageAccessKeyId) &&
            !string.IsNullOrWhiteSpace(request.StorageSecretKey))
        {
            await _storeCredentials(
                request.RepositoryLocation,
                new ObjectStorageCredentials(
                    request.StorageAccessKeyId,
                    request.StorageSecretKey,
                    string.IsNullOrWhiteSpace(request.StorageRegion) ? null : request.StorageRegion),
                cancellationToken);
        }

        // Installed mode hands the whole operation to the service and does not have a second way to
        // do it. The fallback that used to sit here provisioned directly from the desktop process:
        // a user-scoped key instead of a machine one, and a schedule written straight into
        // %ProgramData% by whoever happened to be logged in. Falling back to that when the service is
        // briefly down would mean the privilege boundary held only while nothing went wrong, which is
        // the one condition under which a boundary does not need to hold.
        if (_serviceClient is not null)
        {
            if (!await _serviceClient.IsServiceAvailableAsync(cancellationToken))
            {
                throw new InvalidOperationException(
                    "The Fortiq protection service is not available, so this folder cannot be protected. " +
                    "Start the Fortiq service and try again.");
            }

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

        // Portable mode only. No service is running for this installation, the state directory is the
        // one carried beside the executable, and the key is scoped to the user - which is what
        // portable means and why it is a separate mode rather than a degraded one.
        // Its own directory per attempt, for the same reason as the service path: the provisioning
        // intent guards one unfinished repository, and sharing one directory turned it into a lock
        // that no later attempt could get past.
        var working = Path.Combine(_paths.Working, "provision", Guid.NewGuid().ToString("N"));
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

        return new ProtectedRepositoryResult(id, provisioned.RecoveryMnemonic, provisioned.DeviceUnlockAvailable,
            BackupScheduled: false,
            SchedulingFailure: "Portable mode does not run automatic backups. The repository and recovery kit exist, but your files are not backed up. Install Fortiq and configure protection in installed mode to enable automatic backups.");
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