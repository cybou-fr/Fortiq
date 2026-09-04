using System.Runtime.Versioning;
using Fortiq.Application;
using Fortiq.Infrastructure.Keys;
using Fortiq.Monitoring;
using Fortiq.Operations;
using Fortiq.Provisioning;
using Fortiq.Scheduling;

namespace Fortiq.Recovery.IntegrationTests;

/// <summary>
/// Proving recovery. This is the operation the whole product is arranged around: not that a backup
/// ran, but that the data comes back out of it.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ProvenRestoreTests
{
    private static string HelperPath => Path.Combine(AppContext.BaseDirectory, "Fortiq.PasswordHelper.exe");

    [SkippableFact]
    public async Task ARestoreThatComesBackTurnsAnUnprovenRepositoryIntoARecoverableOne()
    {
        Skip.IfNot(WindowsTpmEnvelope.IsAvailable, "This machine has no platform crypto provider.");
        Skip.IfNot(File.Exists(HelperPath), "The password helper was not built next to the tests.");

        using var workspace = await RecoveryWorkspace.CreateAsync("proven-restore", CancellationToken.None);
        var source = Path.Combine(workspace.Root, "source");
        TestDataset.Create(source);

        var kitDirectory = Path.Combine(workspace.Root, "kit");
        var provisioned = await new RepositoryProvisioner(RecoveryWorkspace.EngineRootPath, HelperPath).CreateAsync(
            workspace.EnsureDirectory("repository"),
            kitDirectory,
            workspace.EnsureDirectory("state-provision"),
            CancellationToken.None);

        var stateDirectory = workspace.EnsureDirectory("state");
        var receipts = workspace.EnsureDirectory("receipts");
        var runs = workspace.EnsureDirectory("runs");
        var schedule = new BackupSchedule(
            "documents",
            provisioned.Repository.Location,
            kitDirectory,
            source,
            "workstation:documents",
            new EveryInterval(TimeSpan.FromHours(6)));

        await WriteScheduleAsync(stateDirectory, schedule);
        var store = new FileSystemScheduleStore(stateDirectory);

        // Something has to be in the repository before there is anything to prove.
        var backup = new UnattendedBackup(
            RecoveryWorkspace.EngineRootPath,
            workspace.EnsureDirectory("backup-work"),
            HelperPath,
            runs,
            receipts);

        await backup.RunAsync(schedule, CancellationToken.None);

        var health = new HealthPublisher(
            store,
            receipts,
            Path.Combine(stateDirectory, "health", "health.json"),
            Path.Combine(stateDirectory, "health", "fortiq.prom"));

        // Backed up and nothing more: this is the state the product refuses to call finished.
        var before = Assert.Single((await health.PublishAsync(CancellationToken.None)).Repositories);
        Assert.Equal(HealthVerdict.Unproven, before.Verdict);
        Assert.Contains(before.Findings, finding => finding.Code == "restore-never-proven");

        var proof = await new ProvenRestore(
            RecoveryWorkspace.EngineRootPath,
            workspace.EnsureDirectory("proof-work"),
            HelperPath,
            runs,
            receipts).ProveAsync(schedule, CancellationToken.None);

        Assert.Equal(provisioned.Repository.Id.ToString(), proof.RepositoryId);
        // Six files in the dataset; the engine's own count is higher because it counts the
        // directories it recreated as well.
        Assert.Equal(6ul, proof.FilesOnDisk);
        Assert.True(proof.NodesRestored >= proof.FilesOnDisk);
        Assert.True(proof.BytesRestored > 0);

        // The proof is evidence on disk, not a value held in memory: a fresh report built from the
        // receipts alone no longer says recovery is unproven.
        var after = Assert.Single((await health.PublishAsync(CancellationToken.None)).Repositories);
        Assert.DoesNotContain(after.Findings, finding => finding.Code == "restore-never-proven");

        // Still not Recoverable, and correctly so: a repository whose integrity has never been
        // checked is not finished either. Proving a restore answers one question, not all of them.
        Assert.Equal(HealthVerdict.Unproven, after.Verdict);
        // What remains is an unchecked repository on local disk, which promises nothing about
        // keeping what is written to it. Neither is about whether the data came back.
        Assert.Equal(
            ["never-checked", "storage-not-immutable"],
            after.Findings.Select(finding => finding.Code).Order(StringComparer.Ordinal));
    }

    [SkippableFact]
    public async Task ARepositoryWithNothingInItCannotBeProven()
    {
        Skip.IfNot(WindowsTpmEnvelope.IsAvailable, "This machine has no platform crypto provider.");
        Skip.IfNot(File.Exists(HelperPath), "The password helper was not built next to the tests.");

        using var workspace = await RecoveryWorkspace.CreateAsync("proven-restore-empty", CancellationToken.None);
        var kitDirectory = Path.Combine(workspace.Root, "kit");
        var provisioned = await new RepositoryProvisioner(RecoveryWorkspace.EngineRootPath, HelperPath).CreateAsync(
            workspace.EnsureDirectory("repository"),
            kitDirectory,
            workspace.EnsureDirectory("state-provision"),
            CancellationToken.None);

        var schedule = new BackupSchedule(
            "documents",
            provisioned.Repository.Location,
            kitDirectory,
            Path.Combine(workspace.Root, "source"),
            "workstation:documents",
            new EveryInterval(TimeSpan.FromHours(6)));

        // An empty repository is not a broken one, and the distinction matters: it must not be
        // reported as proven, and it must not be reported as damaged either.
        var error = await Assert.ThrowsAsync<RestoreProofFailedException>(
            () => new ProvenRestore(
                RecoveryWorkspace.EngineRootPath,
                workspace.EnsureDirectory("proof-work"),
                HelperPath,
                workspace.EnsureDirectory("runs"),
                workspace.EnsureDirectory("receipts")).ProveAsync(schedule, CancellationToken.None));

        Assert.Contains("no snapshots", error.Message, StringComparison.Ordinal);
    }

    private static async Task WriteScheduleAsync(string stateDirectory, BackupSchedule schedule)
    {
        var directory = Path.Combine(stateDirectory, "schedules");
        Directory.CreateDirectory(directory);

        var json = $$"""
            {
              "schema": "fortiq.backup-schedule",
              "version": 1,
              "id": "{{schedule.Id}}",
              "repository": {{System.Text.Json.JsonSerializer.Serialize(schedule.RepositoryLocation)}},
              "kit": {{System.Text.Json.JsonSerializer.Serialize(schedule.KitDirectory)}},
              "source": {{System.Text.Json.JsonSerializer.Serialize(schedule.SourcePath)}},
              "sourceStableId": "{{schedule.SourceStableId}}",
              "recurrence": { "kind": "interval", "period": "06:00:00" }
            }
            """;

        await File.WriteAllTextAsync(Path.Combine(directory, schedule.Id + ".json"), json);
    }
}
