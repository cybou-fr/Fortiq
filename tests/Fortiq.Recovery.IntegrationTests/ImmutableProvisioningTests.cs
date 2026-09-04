using Fortiq.Application;
using Fortiq.Domain;
using Fortiq.Infrastructure.Keys;
using Fortiq.Infrastructure.ObjectStorage;
using Fortiq.Provisioning;

namespace Fortiq.Recovery.IntegrationTests;

/// <summary>
/// Provisioning against storage that was asked what it protects. Object locking can only be turned
/// on when a bucket is made, so a repository that needs it has to be refused up front rather than
/// created and then found wanting.
/// </summary>
public sealed class ImmutableProvisioningTests
{
    private static string HelperPath => Path.Combine(AppContext.BaseDirectory, "Fortiq.PasswordHelper.exe");

    [SkippableFact]
    public async Task ALockedBucketIsAcceptedAndWhatItPromisedIsRecordedInTheKit()
    {
        Skip.IfNot(File.Exists(HelperPath), "The password helper was not built next to the tests.");
        using var workspace = await RecoveryWorkspace.CreateAsync("immutable-provision", CancellationToken.None);
        await using var storage = await ObjectStorageServer.StartAsync(CancellationToken.None);

        var bucket = await storage.CreateLockedBucketAsync(TimeSpan.FromDays(30), CancellationToken.None);
        var credentials = new FixedStorageCredentials(storage.Credentials);
        var kitDirectory = Path.Combine(workspace.Root, "kit");

        var provisioned = await Provisioner(credentials).CreateAsync(
            storage.RepositoryLocationFor(bucket),
            kitDirectory,
            workspace.EnsureDirectory("state"),
            CancellationToken.None,
            addDeviceUnlock: false,
            requireImmutableStorage: true);

        Assert.True(provisioned.StorageProtection.Immutable);
        Assert.Equal(RetentionMode.Compliance, provisioned.StorageProtection.Mode);
        Assert.Equal(TimeSpan.FromDays(30), provisioned.StorageProtection.DefaultRetention);

        // The kit carries what the storage promised, because storage can be changed later and a kit
        // should say what was true when the repository was made.
        var kit = await RecoveryKitStore.ReadAsync(kitDirectory, CancellationToken.None);
        Assert.True(kit.Manifest.StorageProtection!.Immutable);
        Assert.Equal("compliance", kit.Manifest.StorageProtection.Mode);
        Assert.Equal(30, kit.Manifest.StorageProtection.RetentionDays);
    }

    [SkippableFact]
    public async Task ABucketThatKeepsNothingIsRefusedBeforeARepositoryExists()
    {
        Skip.IfNot(File.Exists(HelperPath), "The password helper was not built next to the tests.");
        using var workspace = await RecoveryWorkspace.CreateAsync("immutable-refused", CancellationToken.None);
        await using var storage = await ObjectStorageServer.StartAsync(CancellationToken.None);

        var bucket = await storage.CreatePlainBucketAsync(CancellationToken.None);
        var credentials = new FixedStorageCredentials(storage.Credentials);
        var state = workspace.EnsureDirectory("state");

        var failure = await Assert.ThrowsAsync<StorageNotImmutableException>(
            () => Provisioner(credentials).CreateAsync(
                storage.RepositoryLocationFor(bucket),
                Path.Combine(workspace.Root, "kit"),
                state,
                CancellationToken.None,
                addDeviceUnlock: false,
                requireImmutableStorage: true));

        Assert.Contains("does not keep what is written", failure.Message, StringComparison.Ordinal);

        // Nothing was created: no repository in the bucket, no kit, and no unfinished run to clean up.
        using var client = storage.CreateClient();
        var objects = await client.ListObjectsV2Async(
            new Amazon.S3.Model.ListObjectsV2Request { BucketName = bucket },
            CancellationToken.None);
        Assert.Equal(0, objects.KeyCount);
        Assert.False(Directory.Exists(Path.Combine(workspace.Root, "kit")));
        Assert.False(File.Exists(Path.Combine(state, "provisioning-intent.json")));
    }

    [SkippableFact]
    public async Task WithoutRequiringImmutabilityAPlainBucketIsUsedAndTheKitSaysItIsNotProtected()
    {
        Skip.IfNot(File.Exists(HelperPath), "The password helper was not built next to the tests.");
        using var workspace = await RecoveryWorkspace.CreateAsync("immutable-optional", CancellationToken.None);
        await using var storage = await ObjectStorageServer.StartAsync(CancellationToken.None);

        var bucket = await storage.CreatePlainBucketAsync(CancellationToken.None);
        var kitDirectory = Path.Combine(workspace.Root, "kit");

        var provisioned = await Provisioner(new FixedStorageCredentials(storage.Credentials)).CreateAsync(
            storage.RepositoryLocationFor(bucket),
            kitDirectory,
            workspace.EnsureDirectory("state"),
            CancellationToken.None,
            addDeviceUnlock: false);

        // Allowed, and recorded honestly: a kit that stayed silent here would read as protected.
        Assert.False(provisioned.StorageProtection.Immutable);
        var kit = await RecoveryKitStore.ReadAsync(kitDirectory, CancellationToken.None);
        Assert.False(kit.Manifest.StorageProtection!.Immutable);
        Assert.Equal("none", kit.Manifest.StorageProtection.Mode);
    }

    [SkippableFact]
    public async Task ALocalDirectoryCannotSatisfyARequirementForImmutableStorage()
    {
        Skip.IfNot(File.Exists(HelperPath), "The password helper was not built next to the tests.");
        using var workspace = await RecoveryWorkspace.CreateAsync("immutable-local", CancellationToken.None);

        // A directory on this machine keeps nothing from whoever can write to it, and saying so is
        // more useful than refusing to answer.
        await Assert.ThrowsAsync<StorageNotImmutableException>(
            () => Provisioner(new NoObjectStorageCredentials()).CreateAsync(
                workspace.EnsureDirectory("repository"),
                Path.Combine(workspace.Root, "kit"),
                workspace.EnsureDirectory("state"),
                CancellationToken.None,
                addDeviceUnlock: false,
                requireImmutableStorage: true));
    }

    [Fact]
    public void AnObjectStorageLocationIsBrokenIntoItsParts()
    {
        var address = RepositoryLocation.ParseObjectStorage("s3:http://127.0.0.1:9000/bucket/prefix/deeper");

        Assert.Equal(new Uri("http://127.0.0.1:9000"), address.Endpoint);
        Assert.Equal("bucket", address.Bucket);
        Assert.Equal("prefix/deeper", address.Prefix);

        // An endpoint that has to be guessed is guessed as the protected one.
        Assert.Equal(new Uri("https://s3.example.com"), RepositoryLocation.ParseObjectStorage("s3:s3.example.com/bucket").Endpoint);
        Assert.Throws<ArgumentException>(() => RepositoryLocation.ParseObjectStorage("s3:s3.example.com"));
    }

    private static RepositoryProvisioner Provisioner(IObjectStorageCredentialProvider credentials) => new(
        RecoveryWorkspace.EngineRootPath,
        HelperPath,
        clock: null,
        storage: credentials,
        protection: new S3StorageProtectionInspector(credentials));

    private sealed class FixedStorageCredentials(ObjectStorageCredentials credentials) : IObjectStorageCredentialProvider
    {
        public Task<ObjectStorageCredentials?> ForRepositoryAsync(string repositoryLocation, CancellationToken cancellationToken) =>
            Task.FromResult<ObjectStorageCredentials?>(credentials);
    }
}
