using System.Runtime.Versioning;
using Fortiq.Application;
using Fortiq.Infrastructure.Keys;
using Fortiq.Operations;
using Fortiq.Provisioning;
using Fortiq.Scheduling;

namespace Fortiq.Recovery.IntegrationTests;

/// <summary>
/// Retention on a schedule, against the real engine. Nothing ran retention before this: a Fortiq
/// installation kept every snapshot it had ever taken, forever.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ScheduledRetentionTests
{
    private static string HelperPath => Path.Combine(AppContext.BaseDirectory, "Fortiq.PasswordHelper.exe");

    [SkippableFact]
    public async Task ADueRetentionRunForgetsWhatThePolicyDoesNotKeep()
    {
        Skip.IfNot(WindowsTpmEnvelope.IsAvailable, "This machine has no platform crypto provider.");
        Skip.IfNot(File.Exists(HelperPath), "The password helper was not built next to the tests.");

        using var workspace = await RecoveryWorkspace.CreateAsync("scheduled-retention", CancellationToken.None);
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
            RetentionRecurrence: new EveryInterval(TimeSpan.FromDays(7)),
            Retention: new RetentionPolicy(KeepLast: 1));

        await WriteScheduleAsync(stateDirectory, schedule);
        var store = new FileSystemScheduleStore(stateDirectory);

        var backup = new UnattendedBackup(
            RecoveryWorkspace.EngineRootPath,
            workspace.EnsureDirectory("backup-work"),
            HelperPath,
            runs,
            receipts);

        await backup.RunAsync(schedule, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(source, "second.txt"), "more", CancellationToken.None);
        await backup.RunAsync(schedule, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(source, "third.txt"), "more still", CancellationToken.None);
        var newest = await backup.RunAsync(schedule, CancellationToken.None);

        await store.WriteStateAsync(
            new ScheduleState(schedule.RetentionStateId, LastSuccessAt: DateTimeOffset.UtcNow.AddDays(-8)),
            CancellationToken.None);

        var outcome = Assert.Single(await new ScheduledRetentionRunner(
            store,
            new UnattendedRetention(
                RecoveryWorkspace.EngineRootPath,
                workspace.EnsureDirectory("retention-work"),
                HelperPath,
                runs,
                receipts)).RunDueAsync(CancellationToken.None));

        Assert.Null(outcome.Failure);
        Assert.Equal(DueVerdict.Due, outcome.Verdict);
        Assert.Equal(2, outcome.Removed);

        // The newest snapshot is what the policy kept, and it is still openable. Retention that left
        // the repository unreadable would be the worst possible outcome of housekeeping.
        var kit = await RecoveryKitStore.ReadAsync(kitDirectory, CancellationToken.None);
        var device = kit.Envelopes.Single(envelope => envelope.Suite == WindowsTpmEnvelope.SuiteId);
        using var lease = WindowsTpmEnvelope.Unwrap(device, provisioned.Repository.Id.ToArray());
        var adapter = workspace.Adapter("verify", new PasswordPipeCredentialProvider(HelperPath, lease));

        var remaining = Assert.Single(
            await adapter.ListSnapshotsAsync(new ListSnapshots(provisioned.Repository), CancellationToken.None));

        Assert.Equal(newest.SnapshotId, remaining.Id);

        // Forgetting is evidence like anything else, and monitoring reads the same directory.
        Assert.Contains(
            Directory.EnumerateFiles(receipts, "*.json", SearchOption.AllDirectories),
            path => File.ReadAllText(path).Contains("\"retention\"", StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task RetentionWillNotRunWhileSomethingElseIsUsingTheRepository()
    {
        Skip.IfNot(WindowsTpmEnvelope.IsAvailable, "This machine has no platform crypto provider.");
        Skip.IfNot(File.Exists(HelperPath), "The password helper was not built next to the tests.");

        using var workspace = await RecoveryWorkspace.CreateAsync("retention-busy", CancellationToken.None);
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
            RetentionRecurrence: new EveryInterval(TimeSpan.FromDays(7)),
            Retention: new RetentionPolicy(KeepLast: 1));

        await WriteScheduleAsync(stateDirectory, schedule);
        var store = new FileSystemScheduleStore(stateDirectory);

        await new UnattendedBackup(
            RecoveryWorkspace.EngineRootPath,
            workspace.EnsureDirectory("backup-work"),
            HelperPath,
            runs,
            receipts).RunAsync(schedule, CancellationToken.None);

        await store.WriteStateAsync(
            new ScheduleState(schedule.RetentionStateId, LastSuccessAt: DateTimeOffset.UtcNow.AddDays(-8)),
            CancellationToken.None);

        // Something else is reading this repository, as a restore or a drill would be.
        var registry = new Fortiq.Infrastructure.Runs.FileSystemRepositoryRunRegistry(runs);
        await using var reader = await registry.BeginAsync(
            provisioned.Repository.Id,
            OperationKind.Snapshots,
            Guid.NewGuid(),
            RunExclusivity.Shared,
            CancellationToken.None);

        var outcome = Assert.Single(await new ScheduledRetentionRunner(
            store,
            new UnattendedRetention(
                RecoveryWorkspace.EngineRootPath,
                workspace.EnsureDirectory("retention-work"),
                HelperPath,
                runs,
                receipts)).RunDueAsync(CancellationToken.None));

        // Forgetting snapshots underneath a reader would apply the policy to a repository that is
        // being read, and could remove the very snapshot a drill is restoring from - which would be
        // recorded as recovery failing to prove, a false alarm caused by housekeeping.
        Assert.NotNull(outcome.Failure);
        Assert.Equal(0, outcome.Removed);

        var state = await store.ReadStateAsync(schedule.RetentionStateId, CancellationToken.None);
        Assert.NotNull(state.LastFailure);
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
              "recurrence": { "kind": "interval", "period": "06:00:00" },
              "retentionRecurrence": { "kind": "interval", "period": "7.00:00:00" },
              "retention": { "keepLast": 1 }
            }
            """;

        await File.WriteAllTextAsync(Path.Combine(directory, schedule.Id + ".json"), json, CancellationToken.None);
    }
}
