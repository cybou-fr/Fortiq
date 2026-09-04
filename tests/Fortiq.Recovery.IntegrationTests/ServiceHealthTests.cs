using Fortiq.Operations;
using System.Runtime.Versioning;
using System.Text.Json;
using Fortiq.Infrastructure.Keys;
using Fortiq.Monitoring;
using Fortiq.Provisioning;
using Fortiq.Scheduling;
using Fortiq.Service;

namespace Fortiq.Recovery.IntegrationTests;

/// <summary>
/// What the service publishes about itself after a real scheduled backup. The claim under test is
/// the honest one: a backup that ran is not the same as a repository that is known to recover.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ServiceHealthTests
{
    private static string HelperPath => Path.Combine(AppContext.BaseDirectory, "Fortiq.PasswordHelper.exe");

    [SkippableFact]
    public async Task AfterAScheduledBackupTheReportSaysBackedUpButNotYetProven()
    {
        Skip.IfNot(WindowsTpmEnvelope.IsAvailable, "This machine has no platform crypto provider.");
        Skip.IfNot(File.Exists(HelperPath), "The password helper was not built next to the tests.");

        using var workspace = await RecoveryWorkspace.CreateAsync("service-health", CancellationToken.None);
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
            var receipts = workspace.EnsureDirectory("service-receipts");
            await WriteScheduleAsync(stateDirectory, provisioned.Repository.Location, kitDirectory, source);

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
                    receipts));

            Assert.Null(Assert.Single(await runner.RunDueAsync(CancellationToken.None)).Failure);

            var health = new HealthPublisher(
                store,
                receipts,
                Path.Combine(workspace.Root, "health", "health.json"),
                Path.Combine(workspace.Root, "health", "fortiq.prom"));

            var report = await health.PublishAsync(CancellationToken.None);
            var repository = Assert.Single(report.Repositories);

            // The backup just ran and the kit is there, so nothing is at risk today - and nothing has
            // shown that the data comes back, so it is not called healthy either.
            Assert.Equal(HealthVerdict.Unproven, repository.Verdict);
            Assert.Equal(provisioned.Repository.Id.ToString().ToLowerInvariant(), repository.RepositoryId);
            Assert.Contains(repository.Findings, finding => finding.Code == "restore-never-proven");
            Assert.Contains(repository.Findings, finding => finding.Code == "never-checked");
            Assert.DoesNotContain(repository.Findings, finding => finding.Code == "kit-missing");
            Assert.NotNull(repository.Facts.LastBackupAt);

            // A local directory keeps nothing safe from whoever can write to it, and the report says
            // so rather than staying quiet about it.
            Assert.Contains(repository.Findings, finding => finding.Code == "storage-not-immutable");

            // Both files exist for something else to read, without asking Fortiq anything.
            using var document = JsonDocument.Parse(
                await File.ReadAllTextAsync(Path.Combine(workspace.Root, "health", "health.json")));
            Assert.Equal("unproven", document.RootElement.GetProperty("worst").GetString());

            var metrics = await File.ReadAllTextAsync(Path.Combine(workspace.Root, "health", "fortiq.prom"));
            Assert.Contains("fortiq_repository_recoverable", metrics, StringComparison.Ordinal);
            Assert.Contains("fortiq_repository_last_backup_age_seconds", metrics, StringComparison.Ordinal);
            Assert.DoesNotContain("fortiq_repository_last_restore_proof_age_seconds{", metrics, StringComparison.Ordinal);
        }
        finally
        {
            WindowsTpmEnvelope.DeleteKey(device);
        }
    }

    [SkippableFact]
    public async Task AScheduleWhoseKitIsGoneIsReportedAsAtRisk()
    {
        Skip.IfNot(File.Exists(HelperPath), "The password helper was not built next to the tests.");

        using var workspace = await RecoveryWorkspace.CreateAsync("service-health-no-kit", CancellationToken.None);
        var source = Path.Combine(workspace.Root, "source");
        TestDataset.Create(source);

        var stateDirectory = workspace.EnsureDirectory("scheduler");
        await WriteScheduleAsync(
            stateDirectory,
            workspace.EnsureDirectory("repository"),
            Path.Combine(workspace.Root, "kit-that-was-lost"),
            source);

        var store = new FileSystemScheduleStore(stateDirectory);
        var health = new HealthPublisher(
            store,
            workspace.EnsureDirectory("service-receipts"),
            Path.Combine(workspace.Root, "health", "health.json"),
            Path.Combine(workspace.Root, "health", "fortiq.prom"));

        var repository = Assert.Single((await health.PublishAsync(CancellationToken.None)).Repositories);

        // Without a kit the repository cannot be opened anywhere else, which is worth an alert today.
        Assert.Equal(HealthVerdict.AtRisk, repository.Verdict);
        Assert.Contains(repository.Findings, finding => finding.Code == "kit-missing");
    }

    private static async Task WriteScheduleAsync(string stateDirectory, string repository, string kit, string source)
    {
        var directory = Path.Combine(stateDirectory, "schedules");
        Directory.CreateDirectory(directory);

        var json = $$"""
            {
              "schema": "fortiq.backup-schedule",
              "version": 1,
              "id": "documents",
              "repository": {{JsonSerializer.Serialize(repository)}},
              "kit": {{JsonSerializer.Serialize(kit)}},
              "source": {{JsonSerializer.Serialize(source)}},
              "sourceStableId": "workstation:documents",
              "recurrence": { "kind": "interval", "period": "06:00:00" }
            }
            """;

        await File.WriteAllTextAsync(Path.Combine(directory, "documents.json"), json);
    }
}
