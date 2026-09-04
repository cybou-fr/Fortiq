using Fortiq.Application;

namespace Fortiq.Recovery.IntegrationTests;

/// <summary>
/// Retention: how much history is kept, and what "removing" a snapshot actually removes. Forgetting
/// a snapshot and deleting its data are different acts, and immutable storage allows only the first.
/// </summary>
public sealed class RetentionTests
{
    [SkippableFact]
    public async Task APolicyKeepsWhatItSaysAndForgetsTheRest()
    {
        using var workspace = await RecoveryWorkspace.CreateAsync("retention-keep-last", CancellationToken.None);
        var (adapter, descriptor, snapshots) = await ThreeSnapshotsAsync(workspace);

        var receipt = await adapter.ApplyRetentionAsync(
            new ApplyRetention(descriptor, new RetentionPolicy(KeepLast: 1)),
            CancellationToken.None);

        Assert.Equal([snapshots[^1]], receipt.KeptSnapshotIds);
        Assert.Equal(2, receipt.RemovedSnapshotIds.Count);

        // Forgetting is not pruning: the snapshots are gone from the listing, and their data has not
        // been deleted, which the receipt states rather than implying.
        Assert.False(receipt.Pruned);
        var remaining = await adapter.ListSnapshotsAsync(new ListSnapshots(descriptor), CancellationToken.None);
        Assert.Equal(snapshots[^1], Assert.Single(remaining).Id);
    }

    [SkippableFact]
    public async Task APolicyThatWouldKeepNothingIsRefusedBeforeAnythingIsForgotten()
    {
        using var workspace = await RecoveryWorkspace.CreateAsync("retention-keeps-nothing", CancellationToken.None);
        var (adapter, descriptor, snapshots) = await ThreeSnapshotsAsync(workspace);

        // Forgetting cannot be undone, so a policy that keeps nothing has to be refused while that is
        // still true.
        await Assert.ThrowsAsync<RetentionWouldRemoveEverythingException>(
            () => adapter.ApplyRetentionAsync(new ApplyRetention(descriptor, new RetentionPolicy()), CancellationToken.None));

        var remaining = await adapter.ListSnapshotsAsync(new ListSnapshots(descriptor), CancellationToken.None);
        Assert.Equal(snapshots.Count, remaining.Count);
    }

    [SkippableFact]
    public async Task APolicyThatRemovesNothingDoesNotTouchTheRepository()
    {
        using var workspace = await RecoveryWorkspace.CreateAsync("retention-noop", CancellationToken.None);
        var (adapter, descriptor, snapshots) = await ThreeSnapshotsAsync(workspace);

        var receipt = await adapter.ApplyRetentionAsync(
            new ApplyRetention(descriptor, new RetentionPolicy(KeepLast: 10)),
            CancellationToken.None);

        Assert.Empty(receipt.RemovedSnapshotIds);
        Assert.Equal(snapshots.Count, receipt.KeptSnapshotIds.Count);
        Assert.Equal(snapshots.Count, (await adapter.ListSnapshotsAsync(new ListSnapshots(descriptor), CancellationToken.None)).Count);
    }

    [SkippableFact]
    public async Task PruningRemovesTheDataAndTheRepositoryStaysHealthy()
    {
        using var workspace = await RecoveryWorkspace.CreateAsync("retention-prune", CancellationToken.None);
        var (adapter, descriptor, snapshots) = await ThreeSnapshotsAsync(workspace);
        var repository = Path.Combine(workspace.Root, "repository");
        var before = SizeOf(repository);

        var receipt = await adapter.ApplyRetentionAsync(
            new ApplyRetention(descriptor, new RetentionPolicy(KeepLast: 1), PruneMode.ForgetAndPrune),
            CancellationToken.None);

        Assert.True(receipt.Pruned);
        Assert.Equal(2, receipt.RemovedSnapshotIds.Count);
        Assert.True(SizeOf(repository) < before, "Pruning removed no data.");

        // What is left has to still be restorable; retention that damages the repository is worse
        // than retention that keeps too much.
        Assert.True((await adapter.CheckAsync(new CheckRepository(descriptor), CancellationToken.None)).IsHealthy);
        Assert.Equal(snapshots[^1], Assert.Single(await adapter.ListSnapshotsAsync(new ListSnapshots(descriptor), CancellationToken.None)).Id);
    }

    [SkippableFact]
    public async Task InALockedBucketPruningHidesDataItCannotDestroy()
    {
        using var workspace = await RecoveryWorkspace.CreateAsync("retention-locked", CancellationToken.None);
        await using var storage = await ObjectStorageServer.StartAsync(CancellationToken.None);

        var bucket = await storage.CreateLockedBucketAsync(TimeSpan.FromDays(1), CancellationToken.None);
        var adapter = workspace.Adapter("state", storage: new FixedStorageCredentials(storage.Credentials));
        var descriptor = await adapter.InitializeAsync(
            new InitializeRepository(storage.RepositoryLocationFor(bucket)),
            CancellationToken.None);

        var source = Path.Combine(workspace.Root, "source");
        TestDataset.Create(source);
        var snapshots = new List<string>();
        for (var index = 0; index < 2; index++)
        {
            await File.WriteAllTextAsync(Path.Combine(source, "small.txt"), $"revision {index}");
            snapshots.Add((await adapter.CreateSnapshotAsync(
                new CreateSnapshot(descriptor, source, "test-source"),
                CancellationToken.None)).SnapshotId);
        }

        using var client = storage.CreateClient();
        var beforePrune = await client.ListVersionsAsync(
            new Amazon.S3.Model.ListVersionsRequest { BucketName = bucket },
            CancellationToken.None);

        var receipt = await adapter.ApplyRetentionAsync(
            new ApplyRetention(descriptor, new RetentionPolicy(KeepLast: 1), PruneMode.ForgetAndPrune),
            CancellationToken.None);

        // Object locking protects versions, not names. A delete that does not name a version adds a
        // delete marker instead, which the lock permits, so pruning succeeds here rather than being
        // refused - and what it removed is hidden rather than destroyed.
        Assert.True(receipt.Pruned);
        Assert.Single(receipt.RemovedSnapshotIds);

        var afterPrune = await client.ListVersionsAsync(
            new Amazon.S3.Model.ListVersionsRequest { BucketName = bucket },
            CancellationToken.None);

        Assert.Contains(afterPrune.Versions, version => version.IsDeleteMarker == true);
        foreach (var version in beforePrune.Versions.Where(version => version.IsDeleteMarker != true))
        {
            Assert.Contains(
                afterPrune.Versions,
                surviving => surviving.Key == version.Key && surviving.VersionId == version.VersionId);
        }

        // And what the lock does refuse is the destruction itself: naming a version is refused
        // outright, which is the difference between hiding data and losing it.
        var pack = beforePrune.Versions.First(version => version.Key.StartsWith("data/", StringComparison.Ordinal));
        var request = new Amazon.S3.Model.DeleteObjectsRequest
        {
            BucketName = bucket,
            Objects = [new Amazon.S3.Model.KeyVersion { Key = pack.Key, VersionId = pack.VersionId }]
        };

        var refusal = await Assert.ThrowsAsync<Amazon.S3.DeleteObjectsException>(
            () => client.DeleteObjectsAsync(request, CancellationToken.None));
        Assert.Contains("WORM", Assert.Single(refusal.Response.DeleteErrors).Message, StringComparison.OrdinalIgnoreCase);

        Assert.True((await adapter.CheckAsync(new CheckRepository(descriptor), CancellationToken.None)).IsHealthy);
    }

    private static long SizeOf(string directory) =>
        Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length);

    private static async Task<(Fortiq.Infrastructure.Restic.ResticRepositoryEngine Adapter, Fortiq.Domain.RepositoryDescriptor Descriptor, List<string> Snapshots)>
        ThreeSnapshotsAsync(RecoveryWorkspace workspace)
    {
        var source = Path.Combine(workspace.Root, "source");
        TestDataset.Create(source);

        var adapter = workspace.Adapter("state");
        var descriptor = await adapter.InitializeAsync(
            new InitializeRepository(workspace.EnsureDirectory("repository")),
            CancellationToken.None);

        var snapshots = new List<string>();
        for (var index = 0; index < 3; index++)
        {
            await File.WriteAllTextAsync(Path.Combine(source, "small.txt"), $"revision {index}\n");
            snapshots.Add((await adapter.CreateSnapshotAsync(
                new CreateSnapshot(descriptor, source, "test-source"),
                CancellationToken.None)).SnapshotId);
        }

        return (adapter, descriptor, snapshots);
    }

    private sealed class FixedStorageCredentials(ObjectStorageCredentials credentials) : IObjectStorageCredentialProvider
    {
        public Task<ObjectStorageCredentials?> ForRepositoryAsync(string repositoryLocation, CancellationToken cancellationToken) =>
            Task.FromResult<ObjectStorageCredentials?>(credentials);
    }
}
