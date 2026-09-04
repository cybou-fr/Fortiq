using System.Runtime.Versioning;
using Fortiq.Infrastructure.Keys;
using Fortiq.Monitoring;
using Fortiq.Operations;
using Fortiq.Provisioning;
using Fortiq.Scheduling;

namespace Fortiq.Recovery.IntegrationTests;

/// <summary>
/// A restore drill with nobody present. Until this existed, only a person pressing a button could
/// establish that data comes back, so a repository on a machine nobody logs into stayed unproven
/// forever - honest, but of no use to the person relying on it.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ScheduledDrillTests
{
    private static string HelperPath => Path.Combine(AppContext.BaseDirectory, "Fortiq.PasswordHelper.exe");

    [SkippableFact]
    public async Task ADueDrillProvesRecoveryWithoutAnyHumanSecret()
    {
        Skip.IfNot(WindowsTpmEnvelope.IsAvailable, "This machine has no platform crypto provider.");
        Skip.IfNot(File.Exists(HelperPath), "The password helper was not built next to the tests.");

        using var workspace = await RecoveryWorkspace.CreateAsync("scheduled-drill", CancellationToken.None);
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
            new EveryInterval(TimeSpan.FromHours(6)),
            DrillRecurrence: new EveryInterval(TimeSpan.FromDays(7)));

        await WriteScheduleAsync(stateDirectory, schedule);
        var store = new FileSystemScheduleStore(stateDirectory);

        await new UnattendedBackup(
            RecoveryWorkspace.EngineRootPath,
            workspace.EnsureDirectory("backup-work"),
            HelperPath,
            runs,
            receipts).RunAsync(schedule, CancellationToken.None);

        var health = new HealthPublisher(
            store,
            receipts,
            Path.Combine(stateDirectory, "health", "health.json"),
            Path.Combine(stateDirectory, "health", "fortiq.prom"));

        Assert.Contains(
            Assert.Single((await health.PublishAsync(CancellationToken.None)).Repositories).Findings,
            finding => finding.Code == "restore-never-proven");

        // The drill is due because its last run is eight days behind a seven-day period.
        await store.WriteStateAsync(
            new ScheduleState(schedule.DrillStateId, LastSuccessAt: DateTimeOffset.UtcNow.AddDays(-8)),
            CancellationToken.None);

        var runner = new ScheduledDrillRunner(
            store,
            new UnattendedRestoreDrill(new ProvenRestore(
                RecoveryWorkspace.EngineRootPath,
                workspace.EnsureDirectory("drill-work"),
                HelperPath,
                runs,
                receipts)));

        var outcome = Assert.Single(await runner.RunDueAsync(CancellationToken.None));

        Assert.Null(outcome.Failure);
        Assert.Equal(DueVerdict.Due, outcome.Verdict);
        Assert.NotNull(outcome.SnapshotId);

        // The proof is a receipt on disk, so a report rebuilt from the receipts alone reaches a
        // different conclusion. Nobody typed anything and nobody was logged in.
        Assert.DoesNotContain(
            Assert.Single((await health.PublishAsync(CancellationToken.None)).Repositories).Findings,
            finding => finding.Code == "restore-never-proven");

        var drillState = await store.ReadStateAsync(schedule.DrillStateId, CancellationToken.None);
        Assert.Equal(outcome.SnapshotId, drillState.LastSnapshotId);
        Assert.Null(drillState.LastFailure);

        // The backup's own state was not touched by the drill.
        var backupState = await store.ReadStateAsync(schedule.Id, CancellationToken.None);
        Assert.Null(backupState.LastSuccessAt);
    }

    [SkippableFact]
    public async Task ARepositoryWithNoDrillRecurrenceIsLeftAlone()
    {
        Skip.IfNot(WindowsTpmEnvelope.IsAvailable, "This machine has no platform crypto provider.");
        Skip.IfNot(File.Exists(HelperPath), "The password helper was not built next to the tests.");

        using var workspace = await RecoveryWorkspace.CreateAsync("scheduled-drill-absent", CancellationToken.None);
        var kitDirectory = Path.Combine(workspace.Root, "kit");
        var provisioned = await new RepositoryProvisioner(RecoveryWorkspace.EngineRootPath, HelperPath).CreateAsync(
            workspace.EnsureDirectory("repository"),
            kitDirectory,
            workspace.EnsureDirectory("state-provision"),
            CancellationToken.None);

        var stateDirectory = workspace.EnsureDirectory("state");
        var schedule = new BackupSchedule(
            "documents",
            provisioned.Repository.Location,
            kitDirectory,
            Path.Combine(workspace.Root, "source"),
            "workstation:documents",
            new EveryInterval(TimeSpan.FromHours(6)));

        await WriteScheduleAsync(stateDirectory, schedule);

        var runner = new ScheduledDrillRunner(
            new FileSystemScheduleStore(stateDirectory),
            new UnattendedRestoreDrill(new ProvenRestore(
                RecoveryWorkspace.EngineRootPath,
                workspace.EnsureDirectory("drill-work"),
                HelperPath,
                workspace.EnsureDirectory("runs"),
                workspace.EnsureDirectory("receipts"))));

        // A schedule file that says nothing about drills gets none. Restoring someone's source is
        // not a default to fall into because a field was omitted.
        Assert.Equal(DueVerdict.Disabled, Assert.Single(await runner.RunDueAsync(CancellationToken.None)).Verdict);
    }

    private static async Task WriteScheduleAsync(string stateDirectory, BackupSchedule schedule)
    {
        var directory = Path.Combine(stateDirectory, "schedules");
        Directory.CreateDirectory(directory);

        var drill = schedule.DrillRecurrence is EveryInterval interval
            ? ",\n  \"drillRecurrence\": { \"kind\": \"interval\", \"period\": \"" + interval.Period + "\" }"
            : string.Empty;

        var json = $$"""
            {
              "schema": "fortiq.backup-schedule",
              "version": 1,
              "id": "{{schedule.Id}}",
              "repository": {{System.Text.Json.JsonSerializer.Serialize(schedule.RepositoryLocation)}},
              "kit": {{System.Text.Json.JsonSerializer.Serialize(schedule.KitDirectory)}},
              "source": {{System.Text.Json.JsonSerializer.Serialize(schedule.SourcePath)}},
              "sourceStableId": "{{schedule.SourceStableId}}",
              "recurrence": { "kind": "interval", "period": "06:00:00" }{{drill}}
            }
            """;

        await File.WriteAllTextAsync(Path.Combine(directory, schedule.Id + ".json"), json);
    }
}
