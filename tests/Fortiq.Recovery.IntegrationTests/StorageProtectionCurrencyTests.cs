using System.Runtime.Versioning;
using Amazon.S3;
using Amazon.S3.Model;
using Fortiq.Application;
using Fortiq.Infrastructure.Keys;
using Fortiq.Infrastructure.ObjectStorage;
using Fortiq.Monitoring;
using Fortiq.Operations;
using Fortiq.Provisioning;
using Fortiq.Scheduling;

namespace Fortiq.Recovery.IntegrationTests;

/// <summary>
/// Health reporting what the storage protects <em>now</em>, against real object storage.
/// </summary>
/// <remarks>
/// The report used to state the protection recorded in the kit at provisioning time, which reads as
/// a claim about today. A bucket whose retention had been lifted still showed as immutable — and
/// lifting retention is the first move of somebody preparing to delete the backups, so the moment
/// the report most needed to change was the one moment it could not.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class StorageProtectionCurrencyTests
{
    private static string HelperPath => Path.Combine(AppContext.BaseDirectory, "Fortiq.PasswordHelper.exe");

    [SkippableFact]
    public async Task RemovingRetentionAfterProvisioningIsNoticedByTheNextReport()
    {
        Skip.IfNot(WindowsTpmEnvelope.IsAvailable, "This machine has no platform crypto provider.");
        Skip.IfNot(File.Exists(HelperPath), "The password helper was not built next to the tests.");

        using var workspace = await RecoveryWorkspace.CreateAsync("protection-currency", CancellationToken.None);
        await using var server = await ObjectStorageServer.StartAsync(CancellationToken.None);
        var storage = new FixedStorageCredentials(server.Credentials);
        var inspector = new S3StorageProtectionInspector(storage);

        var bucket = await server.CreateLockedBucketAsync(TimeSpan.FromDays(30), CancellationToken.None);
        var location = server.RepositoryLocationFor(bucket);

        var kitDirectory = Path.Combine(workspace.Root, "kit");
        var provisioned = await new RepositoryProvisioner(
            RecoveryWorkspace.EngineRootPath,
            HelperPath,
            storage: storage,
            protection: inspector).CreateAsync(
                location,
                kitDirectory,
                workspace.EnsureDirectory("state-provision"),
                CancellationToken.None,
                requireImmutableStorage: true);

        Assert.True(provisioned.StorageProtection.Immutable);

        var stateDirectory = workspace.EnsureDirectory("state");
        var receipts = workspace.EnsureDirectory("receipts");
        var schedule = new BackupSchedule(
            "documents",
            location,
            kitDirectory,
            Path.Combine(workspace.Root, "source"),
            "workstation:documents",
            new EveryInterval(TimeSpan.FromHours(6)));

        await WriteScheduleAsync(stateDirectory, schedule);
        var store = new FileSystemScheduleStore(stateDirectory);

        var health = new HealthPublisher(
            store,
            receipts,
            Path.Combine(stateDirectory, "health", "health.json"),
            Path.Combine(stateDirectory, "health", "fortiq.prom"),
            protection: inspector);

        // While the bucket still holds retention, nothing is said about storage at all.
        var before = Assert.Single((await health.PublishAsync(CancellationToken.None)).Repositories);
        Assert.DoesNotContain(before.Findings, finding => finding.Code.StartsWith("storage", StringComparison.Ordinal));

        await RemoveDefaultRetentionAsync(server, bucket, CancellationToken.None);

        // The kit still records that this repository was created on immutable storage, and that is
        // exactly why the report must not read from it: the promise it recorded is no longer kept.
        var after = Assert.Single((await health.PublishAsync(CancellationToken.None)).Repositories);
        var finding = Assert.Single(
            after.Findings,
            candidate => candidate.Code.StartsWith("storage", StringComparison.Ordinal));

        Assert.Equal("storage-protection-lost", finding.Code);

        var kit = await RecoveryKitStore.ReadAsync(kitDirectory, CancellationToken.None);
        Assert.True(kit.Manifest.StorageProtection?.Immutable);
    }

    [SkippableFact]
    public async Task StorageThatCannotBeReachedIsReportedAsUnverifiedRatherThanProtected()
    {
        Skip.IfNot(WindowsTpmEnvelope.IsAvailable, "This machine has no platform crypto provider.");
        Skip.IfNot(File.Exists(HelperPath), "The password helper was not built next to the tests.");

        using var workspace = await RecoveryWorkspace.CreateAsync("protection-unreachable", CancellationToken.None);
        var kitDirectory = Path.Combine(workspace.Root, "kit");
        string location;

        await using (var server = await ObjectStorageServer.StartAsync(CancellationToken.None))
        {
            var storage = new FixedStorageCredentials(server.Credentials);
            var bucket = await server.CreateLockedBucketAsync(TimeSpan.FromDays(30), CancellationToken.None);
            location = server.RepositoryLocationFor(bucket);

            await new RepositoryProvisioner(
                RecoveryWorkspace.EngineRootPath,
                HelperPath,
                storage: storage,
                protection: new S3StorageProtectionInspector(storage)).CreateAsync(
                    location,
                    kitDirectory,
                    workspace.EnsureDirectory("state-provision"),
                    CancellationToken.None,
                    requireImmutableStorage: true);
        }

        // The server is gone. Nothing can be established about the storage now.
        var stateDirectory = workspace.EnsureDirectory("state");
        var schedule = new BackupSchedule(
            "documents",
            location,
            kitDirectory,
            Path.Combine(workspace.Root, "source"),
            "workstation:documents",
            new EveryInterval(TimeSpan.FromHours(6)));

        await WriteScheduleAsync(stateDirectory, schedule);

        var report = await new HealthPublisher(
            new FileSystemScheduleStore(stateDirectory),
            workspace.EnsureDirectory("receipts"),
            Path.Combine(stateDirectory, "health", "health.json"),
            Path.Combine(stateDirectory, "health", "fortiq.prom"),
            protection: new S3StorageProtectionInspector(
                new FixedStorageCredentials(new ObjectStorageCredentials("k", "s", "us-east-1")))).PublishAsync(
                    CancellationToken.None);

        // Unreachable is not the same as unprotected, and it is certainly not protected. The report
        // says it could not tell, which is the only honest answer and the one that still prompts
        // somebody to look.
        var finding = Assert.Single(
            Assert.Single(report.Repositories).Findings,
            candidate => candidate.Code.StartsWith("storage", StringComparison.Ordinal));

        Assert.Equal("storage-protection-unknown", finding.Code);
    }

    /// <summary>Supplies one set of credentials for every location, as an operator's environment does.</summary>
    private sealed class FixedStorageCredentials(ObjectStorageCredentials credentials) : IObjectStorageCredentialProvider
    {
        public Task<ObjectStorageCredentials?> ForRepositoryAsync(string repositoryLocation, CancellationToken cancellationToken) =>
            Task.FromResult<ObjectStorageCredentials?>(credentials);
    }

    private static async Task RemoveDefaultRetentionAsync(
        ObjectStorageServer server,
        string bucket,
        CancellationToken cancellationToken)
    {
        using var client = server.CreateClient();

        // Object Lock stays enabled on the bucket; only the default retention rule is taken away.
        // This is what an administrator or an attacker with credentials actually does, and objects
        // already written keep the retention they were given.
        await client.PutObjectLockConfigurationAsync(
            new PutObjectLockConfigurationRequest
            {
                BucketName = bucket,
                ObjectLockConfiguration = new ObjectLockConfiguration
                {
                    ObjectLockEnabled = ObjectLockEnabled.Enabled
                }
            },
            cancellationToken);
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

        await File.WriteAllTextAsync(Path.Combine(directory, schedule.Id + ".json"), json, cancellationToken: CancellationToken.None);
    }
}
