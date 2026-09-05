using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Fortiq.Application;
using Fortiq.Infrastructure.ObjectStorage;
using Fortiq.Platform.Windows;

namespace Fortiq.Security.Tests;

/// <summary>
/// Storage credentials held per repository and encrypted to the machine, rather than one set of
/// environment variables covering everything.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class StoredCredentialTests : IDisposable
{
    [Fact]
    public async Task CredentialsArePrivateEvenWhenTheParentAllowsOtherUsersToRead()
    {
        var store = new MachineCredentialStore(_directory);
        await store.WriteAsync("subject", "secret", CancellationToken.None);
        var directory = new DirectoryInfo(_directory);
        var acl = directory.GetAccessControl();
        Assert.True(acl.AreAccessRulesProtected);
        var allowed = new[] { acl.GetOwner(typeof(SecurityIdentifier)),
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) };
        foreach (FileSystemAccessRule rule in new FileInfo(Directory.GetFiles(_directory, "*.json").Single())
            .GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            Assert.Contains(rule.IdentityReference, allowed);
        }
        Assert.Equal("secret", await store.ReadAsync("subject", CancellationToken.None));
    }

    [Fact]
    public async Task BroadLegacyFileAccessIsRefusedAndReplacingTheCredentialRepairsIt()
    {
        var store = new MachineCredentialStore(_directory);
        await store.WriteAsync("subject", "secret", CancellationToken.None);
        var file = new FileInfo(Directory.GetFiles(_directory, "*.json").Single());
        var acl = file.GetAccessControl();
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            FileSystemRights.Read, AccessControlType.Allow));
        file.SetAccessControl(acl);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.ReadAsync("subject", CancellationToken.None));
        await store.WriteAsync("subject", "replacement", CancellationToken.None);
        Assert.Equal("replacement", await store.ReadAsync("subject", CancellationToken.None));
    }
    private readonly string _directory = Directory.CreateTempSubdirectory("fortiq-credentials-").FullName;

    [Fact]
    public async Task ACredentialComesBackForTheRepositoryItWasStoredAgainst()
    {
        var store = new StoredObjectStorageCredentials(_directory);
        await store.WriteAsync(
            "s3:https://storage.example/backups",
            new ObjectStorageCredentials("AKIA", "secret", "eu-west-1"),
            CancellationToken.None);

        var read = await store.ForRepositoryAsync("s3:https://storage.example/backups", CancellationToken.None);

        Assert.NotNull(read);
        Assert.Equal("AKIA", read.AccessKeyId);
        Assert.Equal("secret", read.SecretAccessKey);
        Assert.Equal("eu-west-1", read.Region);
    }

    [Fact]
    public async Task OneRepositorysCredentialIsNotAnothersS()
    {
        var store = new StoredObjectStorageCredentials(_directory);
        await store.WriteAsync(
            "s3:https://storage.example/finance",
            new ObjectStorageCredentials("AKIA-FINANCE", "secret", null),
            CancellationToken.None);

        // The point of storing per repository: an identity issued for one bucket does not silently
        // become the identity for every other repository on the machine.
        Assert.Null(await store.ForRepositoryAsync("s3:https://storage.example/marketing", CancellationToken.None));
    }

    [Fact]
    public async Task TheSecretIsNotReadableInTheFile()
    {
        var store = new StoredObjectStorageCredentials(_directory);
        await store.WriteAsync(
            "s3:https://storage.example/backups",
            new ObjectStorageCredentials("AKIA", "a-very-recognisable-secret", null),
            CancellationToken.None);

        foreach (var path in Directory.EnumerateFiles(_directory, "*", SearchOption.AllDirectories))
        {
            var contents = await File.ReadAllTextAsync(path);
            Assert.DoesNotContain("a-very-recognisable-secret", contents, StringComparison.Ordinal);

            // Nor is the access key, and nor is the repository the credential belongs to: a
            // directory listing is readable by more people than the file contents are.
            Assert.DoesNotContain("AKIA", contents, StringComparison.Ordinal);
            Assert.DoesNotContain("storage.example", Path.GetFileName(path), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ACredentialFileMovedIntoAnothersPlaceIsRefused()
    {
        // Written in separate directories so each file can be identified without knowing how the
        // store names them.
        var mine = Path.Combine(_directory, "mine");
        var theirs = Path.Combine(_directory, "theirs");

        await new MachineCredentialStore(mine).WriteAsync("storage:one", "first", CancellationToken.None);
        await new MachineCredentialStore(theirs).WriteAsync("storage:two", "second", CancellationToken.None);

        // Somebody drops another repository's credential file into the place this one is read from.
        File.Copy(
            Directory.GetFiles(theirs, "*.json").Single(),
            Directory.GetFiles(mine, "*.json").Single(),
            overwrite: true);

        // Refused rather than used. Pointing one repository at credentials issued for another is
        // how a write-only identity for one bucket quietly becomes an identity for a different one.
        await Assert.ThrowsAsync<InvalidDataException>(
            () => new MachineCredentialStore(mine).ReadAsync("storage:one", CancellationToken.None));
    }

    [Fact]
    public async Task ALocalDirectoryRepositoryIsNeverGivenStorageCredentials()
    {
        var store = new StoredObjectStorageCredentials(_directory);

        // Asking would invite somebody to configure a credential that is never used, and storing one
        // is refused outright rather than kept and ignored.
        Assert.Null(await store.ForRepositoryAsync(@"C:\backups\repository", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => store.WriteAsync(
                @"C:\backups\repository",
                new ObjectStorageCredentials("AKIA", "secret", null),
                CancellationToken.None));
    }

    [Fact]
    public async Task AStoredCredentialIsPreferredOverTheEnvironment()
    {
        var store = new StoredObjectStorageCredentials(_directory);
        await store.WriteAsync(
            "s3:https://storage.example/backups",
            new ObjectStorageCredentials("AKIA-STORED", "stored", null),
            CancellationToken.None);

        var chained = new FirstAvailableObjectStorageCredentials(store, new FixedProvider("AKIA-ENV"));

        // Specific beats general. A credential recorded for this repository is a deliberate choice;
        // the environment is whatever the machine happened to have.
        var read = await chained.ForRepositoryAsync("s3:https://storage.example/backups", CancellationToken.None);
        Assert.Equal("AKIA-STORED", read!.AccessKeyId);
    }

    [Fact]
    public async Task TheEnvironmentStillAnswersForARepositoryWithNothingStored()
    {
        var chained = new FirstAvailableObjectStorageCredentials(
            new StoredObjectStorageCredentials(_directory),
            new FixedProvider("AKIA-ENV"));

        // The fallback has to keep working, or every test and CI job that exports two variables
        // breaks the day this store appears.
        var read = await chained.ForRepositoryAsync("s3:https://storage.example/backups", CancellationToken.None);
        Assert.Equal("AKIA-ENV", read!.AccessKeyId);
    }

    [Fact]
    public async Task RemovingACredentialLeavesNothingBehind()
    {
        var store = new StoredObjectStorageCredentials(_directory);
        await store.WriteAsync(
            "s3:https://storage.example/backups",
            new ObjectStorageCredentials("AKIA", "secret", null),
            CancellationToken.None);

        Assert.True(store.Remove("s3:https://storage.example/backups"));
        Assert.Null(await store.ForRepositoryAsync("s3:https://storage.example/backups", CancellationToken.None));
        Assert.False(store.Remove("s3:https://storage.example/backups"));
    }

    [Fact]
    public async Task AFileFromAnUnknownVersionIsRefusedRatherThanGuessedAt()
    {
        var store = new MachineCredentialStore(_directory);
        await store.WriteAsync("storage:x", "secret", CancellationToken.None);

        var path = Directory.GetFiles(_directory, "*.json").Single();
        await File.WriteAllTextAsync(
            path,
            (await File.ReadAllTextAsync(path)).Replace("\"version\":1", "\"version\":99", StringComparison.Ordinal));

        // Credentials are the one thing where reading a shape you do not understand is worse than
        // failing: a misread field could send a secret to the wrong endpoint.
        await Assert.ThrowsAsync<InvalidDataException>(
            () => store.ReadAsync("storage:x", CancellationToken.None));
    }

    private sealed class FixedProvider(string accessKey) : IObjectStorageCredentialProvider
    {
        public Task<ObjectStorageCredentials?> ForRepositoryAsync(string repositoryLocation, CancellationToken cancellationToken) =>
            Task.FromResult<ObjectStorageCredentials?>(
                Fortiq.Domain.RepositoryLocation.IsObjectStorage(repositoryLocation)
                    ? new ObjectStorageCredentials(accessKey, "environment", null)
                    : null);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
