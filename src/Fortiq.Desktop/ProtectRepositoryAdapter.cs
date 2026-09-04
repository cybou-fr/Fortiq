using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fortiq.Application;
using Fortiq.Desktop.ViewModels;
using Fortiq.Provisioning;

namespace Fortiq.Desktop;

/// <summary>
/// Turns the wizard's three answers into a repository, a recovery kit and, when possible, a nightly schedule.
/// </summary>
/// <remarks>
/// Once provisioning returns, the recovery mnemonic cannot be reproduced. A later schedule failure
/// therefore returns a degraded success: the UI must show the mnemonic and explicitly say that
/// unattended backup was not configured. Throwing at that point would strand a repository whose
/// only disaster secret never reached the person who created it.
/// </remarks>
public sealed class ProtectRepositoryAdapter : IProtectRepository
{
    private readonly RepositoryProvisioner _provisioner;
    private readonly FortiqStatePaths _paths;
    private readonly TimeOnly _nightly;

    public ProtectRepositoryAdapter(RepositoryProvisioner provisioner, FortiqStatePaths paths, TimeOnly? nightly = null)
    {
        _provisioner = provisioner ?? throw new ArgumentNullException(nameof(provisioner));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _nightly = nightly ?? new TimeOnly(2, 30);
    }

    public async Task<ProtectedRepositoryResult> CreateAsync(
        ProtectRepositoryRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

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
    /// Writes the schedule file the service reads. Public and static so a test can write one and read
    /// it back through <c>FileSystemScheduleStore</c>: this file is a contract between two projects
    /// that never call each other, and nothing else would notice the two drifting apart.
    /// </summary>
    public static async Task WriteScheduleAsync(
        string directory,
        string repositoryId,
        ProtectRepositoryRequest request,
        TimeOnly nightly,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(request);
        Directory.CreateDirectory(directory);

        var document = new JsonObject
        {
            ["schema"] = "fortiq.backup-schedule",
            ["version"] = 1,
            ["id"] = repositoryId,
            ["repository"] = request.RepositoryLocation,
            ["kit"] = Path.GetFullPath(request.KitDirectory),
            ["source"] = Path.GetFullPath(request.SourcePath),
            ["sourceStableId"] = Path.GetFullPath(request.SourcePath),
            ["recurrence"] = new JsonObject
            {
                ["kind"] = "dailyAt",
                ["timeOfDay"] = nightly.ToString("HH:mm", CultureInfo.InvariantCulture),
                ["timeZone"] = TimeZoneInfo.Local.Id
            },
            // Live rather than snapshot: a volume snapshot needs elevation, and a schedule that is
            // silently unable to run is worse than one that copies files as they are.
            // A restore drill every seven days. The health model stops calling a repository proven
            // after thirty-one days without one, so weekly leaves room for several failed attempts
            // before anyone is told the repository is unproven again.
            ["drillRecurrence"] = new JsonObject
            {
                ["kind"] = "interval",
                ["period"] = "7.00:00:00"
            },
            ["consistency"] = "live",
            ["catchUp"] = "once",
            ["enabled"] = true
        };

        var path = Path.Combine(directory, repositoryId + ".json");
        var temporary = path + ".partial";
        await File.WriteAllTextAsync(
            temporary,
            document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken);

        File.Move(temporary, path, overwrite: true);
    }
}
