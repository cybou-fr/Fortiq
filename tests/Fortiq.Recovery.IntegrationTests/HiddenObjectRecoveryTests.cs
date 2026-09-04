using Amazon.S3.Model;
using Fortiq.Application;
using Fortiq.Domain;
using Fortiq.Infrastructure.ObjectStorage;

namespace Fortiq.Recovery.IntegrationTests;

/// <summary>
/// Recovering a repository that was hidden rather than destroyed. A locked bucket refuses to delete a
/// version, but permits a delete that names only a key: the object stops being visible while every
/// version of it survives. This is what brings it back.
/// </summary>
public sealed class HiddenObjectRecoveryTests
{
    [SkippableFact]
    public async Task ARepositoryHiddenBehindDeleteMarkersIsBroughtBackAndRestoresItsData()
    {
        using var workspace = await RecoveryWorkspace.CreateAsync("hidden-recovery", CancellationToken.None);
        await using var storage = await ObjectStorageServer.StartAsync(CancellationToken.None);

        var source = Path.Combine(workspace.Root, "source");
        var expected = TestDataset.Create(source);
        var bucket = await storage.CreateLockedBucketAsync(TimeSpan.FromDays(1), CancellationToken.None);
        var credentials = new FixedStorageCredentials(storage.Credentials);
        var location = storage.RepositoryLocationFor(bucket);

        var adapter = workspace.Adapter("state", storage: credentials);
        var descriptor = await adapter.InitializeAsync(new InitializeRepository(location), CancellationToken.None);
        var backup = await adapter.CreateSnapshotAsync(
            new CreateSnapshot(descriptor, source, "test-source"),
            CancellationToken.None);

        var recovery = new S3HiddenObjectRecovery(credentials);
        Assert.False((await recovery.InspectAsync(location, CancellationToken.None)).AnythingHidden);

        // The attack: credentials that can write are used to delete by key. The storage allows it,
        // because a delete marker is not the destruction of a version.
        using (var client = storage.CreateClient())
        {
            var objects = await client.ListObjectsV2Async(
                new ListObjectsV2Request { BucketName = bucket },
                CancellationToken.None);

            foreach (var entry in objects.S3Objects)
            {
                await client.DeleteObjectAsync(bucket, entry.Key, CancellationToken.None);
            }
        }

        await Assert.ThrowsAnyAsync<Exception>(
            () => adapter.ListSnapshotsAsync(new ListSnapshots(descriptor), CancellationToken.None));

        var hidden = await recovery.InspectAsync(location, CancellationToken.None);
        Assert.True(hidden.AnythingHidden);
        Assert.True(hidden.VersioningAvailable);
        Assert.Equal(hidden.HiddenCount, hidden.RecoverableCount);

        var restored = await recovery.RestoreAsync(location, CancellationToken.None);
        Assert.Equal(hidden.HiddenCount, restored.RestoredCount);
        Assert.Equal(0, restored.StillHiddenCount);

        // The repository is not merely visible again: it is whole, and the data comes back byte for
        // byte, which is the only version of "recovered" that counts.
        Assert.True((await adapter.CheckAsync(new CheckRepository(descriptor), CancellationToken.None)).IsHealthy);
        Assert.Equal(
            backup.SnapshotId,
            Assert.Single(await adapter.ListSnapshotsAsync(new ListSnapshots(descriptor), CancellationToken.None)).Id);

        var target = workspace.EnsureDirectory("restored");
        await adapter.RestoreAsync(
            new RestoreSnapshot(descriptor, backup.SnapshotId, target, source),
            CancellationToken.None);

        foreach (var entry in expected)
        {
            var file = Path.Combine(target, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.Equal(entry.Sha256, TestDataset.HashFile(file));
        }
    }

    [SkippableFact]
    public async Task RecoveryNeverRemovesAVersionThatHoldsData()
    {
        using var workspace = await RecoveryWorkspace.CreateAsync("hidden-safety", CancellationToken.None);
        await using var storage = await ObjectStorageServer.StartAsync(CancellationToken.None);

        var source = Path.Combine(workspace.Root, "source");
        TestDataset.Create(source);
        var bucket = await storage.CreateLockedBucketAsync(TimeSpan.FromDays(1), CancellationToken.None);
        var credentials = new FixedStorageCredentials(storage.Credentials);
        var location = storage.RepositoryLocationFor(bucket);

        var adapter = workspace.Adapter("state", storage: credentials);
        var descriptor = await adapter.InitializeAsync(new InitializeRepository(location), CancellationToken.None);
        await adapter.CreateSnapshotAsync(new CreateSnapshot(descriptor, source, "test-source"), CancellationToken.None);

        using var inspector = storage.CreateClient();
        var before = await inspector.ListVersionsAsync(new ListVersionsRequest { BucketName = bucket }, CancellationToken.None);
        var dataVersions = before.Versions.Where(version => version.IsDeleteMarker != true).ToArray();

        using (var client = storage.CreateClient())
        {
            var objects = await client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket }, CancellationToken.None);
            await client.DeleteObjectAsync(bucket, objects.S3Objects[0].Key, CancellationToken.None);
        }

        await new S3HiddenObjectRecovery(credentials).RestoreAsync(location, CancellationToken.None);

        var after = await inspector.ListVersionsAsync(new ListVersionsRequest { BucketName = bucket }, CancellationToken.None);
        foreach (var version in dataVersions)
        {
            Assert.Contains(
                after.Versions,
                surviving => surviving.Key == version.Key && surviving.VersionId == version.VersionId);
        }

        // No content is hidden any more. Markers over locks are left as they are: the engine deletes
        // its own locks when it finishes, and restoring one would block the repository.
        Assert.DoesNotContain(
            after.Versions,
            version => version.IsDeleteMarker == true
                && version.IsLatest == true
                && !version.Key.StartsWith("locks/", StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task RecoveringARepositoryThatIsNotHiddenChangesNothing()
    {
        using var workspace = await RecoveryWorkspace.CreateAsync("hidden-noop", CancellationToken.None);
        await using var storage = await ObjectStorageServer.StartAsync(CancellationToken.None);

        var source = Path.Combine(workspace.Root, "source");
        TestDataset.Create(source);
        var bucket = await storage.CreateLockedBucketAsync(TimeSpan.FromDays(1), CancellationToken.None);
        var credentials = new FixedStorageCredentials(storage.Credentials);
        var location = storage.RepositoryLocationFor(bucket);

        var adapter = workspace.Adapter("state", storage: credentials);
        var descriptor = await adapter.InitializeAsync(new InitializeRepository(location), CancellationToken.None);
        await adapter.CreateSnapshotAsync(new CreateSnapshot(descriptor, source, "test-source"), CancellationToken.None);

        var restored = await new S3HiddenObjectRecovery(credentials).RestoreAsync(location, CancellationToken.None);

        Assert.Equal(0, restored.RestoredCount);
        Assert.Equal(0, restored.StillHiddenCount);
        Assert.True((await adapter.CheckAsync(new CheckRepository(descriptor), CancellationToken.None)).IsHealthy);
    }

    [SkippableFact]
    public async Task InABucketWithoutVersioningNothingCanBeBroughtBack()
    {
        using var workspace = await RecoveryWorkspace.CreateAsync("hidden-unversioned", CancellationToken.None);
        await using var storage = await ObjectStorageServer.StartAsync(CancellationToken.None);

        var source = Path.Combine(workspace.Root, "source");
        TestDataset.Create(source);
        var bucket = await storage.CreatePlainBucketAsync(CancellationToken.None);
        var credentials = new FixedStorageCredentials(storage.Credentials);
        var location = storage.RepositoryLocationFor(bucket);

        var adapter = workspace.Adapter("state", storage: credentials);
        var descriptor = await adapter.InitializeAsync(new InitializeRepository(location), CancellationToken.None);
        await adapter.CreateSnapshotAsync(new CreateSnapshot(descriptor, source, "test-source"), CancellationToken.None);

        using (var client = storage.CreateClient())
        {
            var objects = await client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket }, CancellationToken.None);
            await client.DeleteObjectAsync(bucket, objects.S3Objects[0].Key, CancellationToken.None);
        }

        // Without versioning a delete is the end of the object, so there is nothing hidden to find
        // and nothing to bring back. Saying that plainly is the point.
        var hidden = await new S3HiddenObjectRecovery(credentials).InspectAsync(location, CancellationToken.None);
        Assert.False(hidden.VersioningAvailable);
        Assert.False(hidden.AnythingHidden);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => adapter.CheckAsync(new CheckRepository(descriptor), CancellationToken.None));
    }

    [Fact]
    public async Task ALocalRepositoryHasNoHiddenObjectsToRecover()
    {
        var recovery = new S3HiddenObjectRecovery(new NoObjectStorageCredentials());

        await Assert.ThrowsAsync<ArgumentException>(
            () => recovery.InspectAsync(Path.GetFullPath("repository"), CancellationToken.None));
    }

    private sealed class FixedStorageCredentials(ObjectStorageCredentials credentials) : IObjectStorageCredentialProvider
    {
        public Task<ObjectStorageCredentials?> ForRepositoryAsync(string repositoryLocation, CancellationToken cancellationToken) =>
            Task.FromResult<ObjectStorageCredentials?>(credentials);
    }
}
