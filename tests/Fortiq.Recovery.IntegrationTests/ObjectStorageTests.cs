using Amazon.S3;
using Amazon.S3.Model;
using Fortiq.Application;

namespace Fortiq.Recovery.IntegrationTests;

/// <summary>
/// A repository in object storage, and what an immutable bucket does to it. These run against a real
/// S3 server on this machine, so what is asserted is the storage's behaviour rather than a belief
/// about it.
/// </summary>
public sealed class ObjectStorageTests
{
    [SkippableFact]
    public async Task ARepositoryInObjectStorageBacksUpAndRestores()
    {
        using var workspace = await RecoveryWorkspace.CreateAsync("s3-round-trip", CancellationToken.None);
        await using var storage = await ObjectStorageServer.StartAsync(CancellationToken.None);

        var source = Path.Combine(workspace.Root, "source");
        var expected = TestDataset.Create(source);
        var bucket = await storage.CreatePlainBucketAsync(CancellationToken.None);

        var adapter = workspace.Adapter("state", storage: new FixedStorageCredentials(storage.Credentials));
        var descriptor = await adapter.InitializeAsync(
            new InitializeRepository(storage.RepositoryLocationFor(bucket)),
            CancellationToken.None);

        // The location is a bucket, not a path: it must survive untouched rather than being resolved
        // against the current directory.
        Assert.Equal(storage.RepositoryLocationFor(bucket), descriptor.Location);

        var backup = await adapter.CreateSnapshotAsync(
            new CreateSnapshot(descriptor, source, "workstation:documents"),
            CancellationToken.None);

        var snapshot = Assert.Single(await adapter.ListSnapshotsAsync(new ListSnapshots(descriptor), CancellationToken.None));
        Assert.Equal(backup.SnapshotId, snapshot.Id);
        Assert.Equal("workstation:documents", snapshot.SourceStableId);

        Assert.True((await adapter.CheckAsync(new CheckRepository(descriptor), CancellationToken.None)).IsHealthy);

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
    public async Task WhatABackupWroteToALockedBucketCannotBeDeleted()
    {
        using var workspace = await RecoveryWorkspace.CreateAsync("s3-object-lock", CancellationToken.None);
        await using var storage = await ObjectStorageServer.StartAsync(CancellationToken.None);

        var source = Path.Combine(workspace.Root, "source");
        TestDataset.Create(source);
        var bucket = await storage.CreateLockedBucketAsync(TimeSpan.FromDays(1), CancellationToken.None);

        var adapter = workspace.Adapter("state", storage: new FixedStorageCredentials(storage.Credentials));
        var descriptor = await adapter.InitializeAsync(
            new InitializeRepository(storage.RepositoryLocationFor(bucket)),
            CancellationToken.None);
        await adapter.CreateSnapshotAsync(new CreateSnapshot(descriptor, source, "test-source"), CancellationToken.None);

        using var client = storage.CreateClient();
        var objects = await client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket }, CancellationToken.None);
        var pack = objects.S3Objects.First(entry => entry.Key.StartsWith("data/", StringComparison.Ordinal));

        // This is the promise: an endpoint that has been taken over holds credentials that can write
        // to the bucket, and still cannot remove what is already there.
        var request = new DeleteObjectsRequest
        {
            BucketName = bucket,
            Objects = [new KeyVersion { Key = pack.Key, VersionId = await CurrentVersionAsync(client, bucket, pack.Key) }]
        };

        var failure = await Assert.ThrowsAsync<DeleteObjectsException>(
            () => client.DeleteObjectsAsync(request, CancellationToken.None));

        // Nothing was removed at all: the response carries no deleted objects, only the refusal.
        Assert.True(failure.Response.DeletedObjects is null or { Count: 0 });
        var refusal = Assert.Single(failure.Response.DeleteErrors);
        Assert.Equal(pack.Key, refusal.Key);
        Assert.Contains("WORM", refusal.Message, StringComparison.OrdinalIgnoreCase);

        // The repository is intact afterwards, which is what makes the refusal worth having.
        Assert.True((await adapter.CheckAsync(new CheckRepository(descriptor), CancellationToken.None)).IsHealthy);
    }

    [SkippableFact]
    public async Task ABucketWithoutObjectLockDoesNotProtectAnything()
    {
        using var workspace = await RecoveryWorkspace.CreateAsync("s3-no-lock", CancellationToken.None);
        await using var storage = await ObjectStorageServer.StartAsync(CancellationToken.None);

        var source = Path.Combine(workspace.Root, "source");
        TestDataset.Create(source);
        var bucket = await storage.CreatePlainBucketAsync(CancellationToken.None);

        var adapter = workspace.Adapter("state", storage: new FixedStorageCredentials(storage.Credentials));
        var descriptor = await adapter.InitializeAsync(
            new InitializeRepository(storage.RepositoryLocationFor(bucket)),
            CancellationToken.None);
        await adapter.CreateSnapshotAsync(new CreateSnapshot(descriptor, source, "test-source"), CancellationToken.None);

        using var client = storage.CreateClient();
        var objects = await client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket }, CancellationToken.None);
        var pack = objects.S3Objects.First(entry => entry.Key.StartsWith("data/", StringComparison.Ordinal));

        await client.DeleteObjectAsync(bucket, pack.Key, CancellationToken.None);

        // Encryption does not make a backup durable: without object locking, credentials that can
        // write can also destroy, and the repository is damaged.
        await Assert.ThrowsAsync<InvalidDataException>(
            () => adapter.CheckAsync(new CheckRepository(descriptor), CancellationToken.None));
    }

    private static async Task<string> CurrentVersionAsync(IAmazonS3 client, string bucket, string key)
    {
        var versions = await client.ListVersionsAsync(new ListVersionsRequest { BucketName = bucket, Prefix = key });
        return versions.Versions.First(version => version.Key == key && version.IsLatest == true).VersionId;
    }

    private sealed class FixedStorageCredentials(ObjectStorageCredentials credentials) : IObjectStorageCredentialProvider
    {
        public Task<ObjectStorageCredentials?> ForRepositoryAsync(string repositoryLocation, CancellationToken cancellationToken) =>
            Task.FromResult<ObjectStorageCredentials?>(credentials);
    }
}
