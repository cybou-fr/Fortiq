using Fortiq.Operations;
using System.Runtime.Versioning;
using Fortiq.Application;
using Fortiq.Infrastructure.Keys;
using Fortiq.Provisioning;
using Fortiq.Scheduling;
using Fortiq.Service;

namespace Fortiq.Recovery.IntegrationTests;

/// <summary>
/// A scheduled backup with nobody present: the machine's own device-bound key opens the repository,
/// and a kit that has no such key fails plainly rather than waiting for a secret no one is there to
/// type.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class UnattendedScheduleTests
{
    private static string HelperPath => Path.Combine(AppContext.BaseDirectory, "Fortiq.PasswordHelper.exe");

    [SkippableFact]
    public async Task ADueScheduleBacksUpWithoutAnyHumanSecret()
    {
        Skip.IfNot(WindowsTpmEnvelope.IsAvailable, "This machine has no platform crypto provider.");
        Skip.IfNot(File.Exists(HelperPath), "The password helper was not built next to the tests.");

        using var workspace = await RecoveryWorkspace.CreateAsync("unattended-schedule", CancellationToken.None);
        var source = Path.Combine(workspace.Root, "source");
        TestDataset.Create(source);

        var kitDirectory = Path.Combine(workspace.Root, "kit");
        var provisioned = await new RepositoryProvisioner(RecoveryWorkspace.EngineRootPath, HelperPath).CreateAsync(
            workspace.EnsureDirectory("repository"),
            kitDirectory,
            workspace.EnsureDirectory("state-provision"),
            CancellationToken.None);

        var opened = await RecoveryKitStore.ReadAsync(kitDirectory, CancellationToken.None);
        var device = opened.Envelopes.Single(envelope => envelope.Suite == WindowsTpmEnvelope.SuiteId);
        try
        {
            var stateDirectory = workspace.EnsureDirectory("scheduler");
            await WriteScheduleAsync(stateDirectory, "documents", provisioned.Repository.Location, kitDirectory, source);

            var store = new FileSystemScheduleStore(stateDirectory);

            // The schedule is due because it has never run and its first occurrence has passed: the
            // clock is set an hour ahead of the state written below.
            await store.WriteStateAsync(
                new ScheduleState("documents", LastSuccessAt: DateTimeOffset.UtcNow.AddHours(-7)),
                CancellationToken.None);

            var runner = new ScheduledBackupRunner(
                store,
                new UnattendedBackup(
                    RecoveryWorkspace.EngineRootPath,
                    workspace.EnsureDirectory("service-work"),
                    HelperPath,
                    workspace.EnsureDirectory("runs"),
                    workspace.EnsureDirectory("service-receipts")));

            var outcome = Assert.Single(await runner.RunDueAsync(CancellationToken.None));

            Assert.Null(outcome.Failure);
            Assert.Equal(DueVerdict.Due, outcome.Verdict);
            Assert.NotNull(outcome.SnapshotId);

            // The snapshot is really in the repository, with the identity and consistency the
            // schedule asked for.
            using var lease = WindowsTpmEnvelope.Unwrap(device, provisioned.Repository.Id.ToArray());
            var adapter = workspace.Adapter("verify", new PasswordPipeCredentialProvider(HelperPath, lease));
            var snapshot = Assert.Single(
                await adapter.ListSnapshotsAsync(new ListSnapshots(provisioned.Repository), CancellationToken.None));

            Assert.Equal(outcome.SnapshotId, snapshot.Id);
            Assert.Equal("workstation:documents", snapshot.SourceStableId);
            Assert.False(snapshot.PointInTime);

            // The run recorded its success where the next pass will read it.
            var state = await store.ReadStateAsync("documents", CancellationToken.None);
            Assert.Equal(outcome.SnapshotId, state.LastSnapshotId);
            Assert.Null(state.LastFailure);
            Assert.NotEmpty(Directory.GetFiles(Path.Combine(workspace.Root, "service-receipts"), "*.json"));
        }
        finally
        {
            WindowsTpmEnvelope.DeleteKey(device);
        }
    }

    [SkippableFact]
    public async Task AKitWithNoDeviceKeyFailsInsteadOfWaitingForAPerson()
    {
        Skip.IfNot(File.Exists(HelperPath), "The password helper was not built next to the tests.");

        using var workspace = await RecoveryWorkspace.CreateAsync("unattended-no-device", CancellationToken.None);
        var source = Path.Combine(workspace.Root, "source");
        TestDataset.Create(source);

        var kitDirectory = Path.Combine(workspace.Root, "kit");
        var provisioned = await new RepositoryProvisioner(RecoveryWorkspace.EngineRootPath, HelperPath).CreateAsync(
            workspace.EnsureDirectory("repository"),
            kitDirectory,
            workspace.EnsureDirectory("state-provision"),
            CancellationToken.None,
            addDeviceUnlock: false);

        var stateDirectory = workspace.EnsureDirectory("scheduler");
        await WriteScheduleAsync(stateDirectory, "documents", provisioned.Repository.Location, kitDirectory, source);

        var store = new FileSystemScheduleStore(stateDirectory);
        await store.WriteStateAsync(
            new ScheduleState("documents", LastSuccessAt: DateTimeOffset.UtcNow.AddHours(-7)),
            CancellationToken.None);

        var runner = new ScheduledBackupRunner(
            store,
            new UnattendedBackup(
                RecoveryWorkspace.EngineRootPath,
                workspace.EnsureDirectory("service-work"),
                HelperPath,
                workspace.EnsureDirectory("runs"),
                workspace.EnsureDirectory("service-receipts")));

        var outcome = Assert.Single(await runner.RunDueAsync(CancellationToken.None));

        // The mnemonic is the way back into a repository from a machine that lost everything. A
        // service that could use it unattended would have to hold it, which would turn it into a
        // secret on the machine rather than one about it.
        Assert.Null(outcome.SnapshotId);
        Assert.Contains("device-bound", outcome.Failure!, StringComparison.Ordinal);

        var state = await store.ReadStateAsync("documents", CancellationToken.None);
        Assert.Contains("device-bound", state.LastFailure!, StringComparison.Ordinal);
    }

    private static async Task WriteScheduleAsync(
        string stateDirectory,
        string id,
        string repository,
        string kit,
        string source)
    {
        var directory = Path.Combine(stateDirectory, "schedules");
        Directory.CreateDirectory(directory);

        var json = $$"""
            {
              "schema": "fortiq.backup-schedule",
              "version": 1,
              "id": "{{id}}",
              "repository": {{System.Text.Json.JsonSerializer.Serialize(repository)}},
              "kit": {{System.Text.Json.JsonSerializer.Serialize(kit)}},
              "source": {{System.Text.Json.JsonSerializer.Serialize(source)}},
              "sourceStableId": "workstation:documents",
              "recurrence": { "kind": "interval", "period": "06:00:00" }
            }
            """;

        await File.WriteAllTextAsync(Path.Combine(directory, id + ".json"), json);
    }
}
