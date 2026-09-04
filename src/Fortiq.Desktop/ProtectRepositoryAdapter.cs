using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fortiq.Desktop.ViewModels;
using Fortiq.Provisioning;

namespace Fortiq.Desktop;

/// <summary>
/// Turns the wizard's three answers into a repository, a recovery kit and a nightly schedule.
/// </summary>
/// <remarks>
/// The schedule is written as part of the same action deliberately. A repository that exists but is
/// backing nothing up is the failure this product is about: it looks protected and is not. If the
/// schedule cannot be written the whole step is reported as failed, so nobody is told they are
/// protected on the strength of an empty repository.
/// </remarks>
public sealed class ProtectRepositoryAdapter : IProtectRepository
{
    private readonly RepositoryProvisioner _provisioner;
    private readonly string _stateDirectory;
    private readonly TimeOnly _nightly;

    public ProtectRepositoryAdapter(RepositoryProvisioner provisioner, string stateDirectory, TimeOnly? nightly = null)
    {
        _provisioner = provisioner ?? throw new ArgumentNullException(nameof(provisioner));
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        _stateDirectory = Path.GetFullPath(stateDirectory);
        _nightly = nightly ?? new TimeOnly(2, 30);
    }

    public async Task<ProtectedRepositoryResult> CreateAsync(
        ProtectRepositoryRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var working = Path.Combine(_stateDirectory, "work");
        Directory.CreateDirectory(working);

        var provisioned = await _provisioner.CreateAsync(
            request.RepositoryLocation,
            request.KitDirectory,
            working,
            cancellationToken);

        var id = provisioned.Repository.Id.ToString();
        await WriteScheduleAsync(Path.Combine(_stateDirectory, "schedules"), id, request, _nightly, cancellationToken);

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
