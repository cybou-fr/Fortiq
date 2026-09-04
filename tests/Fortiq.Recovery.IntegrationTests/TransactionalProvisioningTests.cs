using System.Text.Json;
using Fortiq.Application;
using Fortiq.Infrastructure.Keys;
using Fortiq.Provisioning;

namespace Fortiq.Recovery.IntegrationTests;

/// <summary>
/// The provisioning invariant: no recoverable kit means no surviving initialised repository. A run
/// either produces a kit that was proven to open the repository, or leaves nothing that looks like
/// one behind.
/// </summary>
public sealed class TransactionalProvisioningTests
{
    private const string IntentFile = "provisioning-intent.json";

    private static string HelperPath => Path.Combine(AppContext.BaseDirectory, "Fortiq.PasswordHelper.exe");

    [SkippableFact]
    public async Task AFailureAfterInitialisationLeavesNoRepositoryBehind()
    {
        RequireHelper();
        using var workspace = await RecoveryWorkspace.CreateAsync("provision-rollback", CancellationToken.None);
        var repository = Path.Combine(workspace.Root, "repository");
        var kit = Path.Combine(workspace.Root, "kit");
        var state = workspace.EnsureDirectory("state");

        var provisioner = new RepositoryProvisioner(RecoveryWorkspace.EngineRootPath, HelperPath)
        {
            AfterInitialize = _ => throw new IOException("the disk went away")
        };

        await Assert.ThrowsAsync<IOException>(
            () => provisioner.CreateAsync(repository, kit, state, CancellationToken.None, addDeviceUnlock: false));

        // The repository existed a moment earlier - restic had initialised it - and none of it may
        // survive a run that produced no kit.
        Assert.False(File.Exists(Path.Combine(repository, "config")));
        Assert.False(Directory.Exists(repository));
        Assert.False(Directory.Exists(kit));
        Assert.False(File.Exists(Path.Combine(state, IntentFile)));
    }

    [SkippableFact]
    public async Task AKitThatCannotBeWrittenLeavesNoRepositoryBehind()
    {
        RequireHelper();
        using var workspace = await RecoveryWorkspace.CreateAsync("provision-kit-failure", CancellationToken.None);
        var repository = Path.Combine(workspace.Root, "repository");
        var state = workspace.EnsureDirectory("state");

        // A file where the kit directory should be makes every kit write fail.
        var kit = Path.Combine(workspace.Root, "kit");
        await File.WriteAllTextAsync(kit, "not a directory");

        var provisioner = new RepositoryProvisioner(RecoveryWorkspace.EngineRootPath, HelperPath);

        await Assert.ThrowsAnyAsync<IOException>(
            () => provisioner.CreateAsync(repository, kit, state, CancellationToken.None, addDeviceUnlock: false));

        Assert.False(File.Exists(Path.Combine(repository, "config")));
        Assert.False(File.Exists(Path.Combine(state, IntentFile)));
    }

    [SkippableFact]
    public async Task ASuccessfulRunProvesTheKitBeforeReporting()
    {
        RequireHelper();
        using var workspace = await RecoveryWorkspace.CreateAsync("provision-proof", CancellationToken.None);
        var repository = Path.Combine(workspace.Root, "repository");
        var kit = Path.Combine(workspace.Root, "kit");
        var state = workspace.EnsureDirectory("state");

        var provisioner = new RepositoryProvisioner(RecoveryWorkspace.EngineRootPath, HelperPath);
        var provisioned = await provisioner.CreateAsync(repository, kit, state, CancellationToken.None, addDeviceUnlock: false);

        // The run reports success only after opening the repository with the kit it just wrote, so
        // repeating that here has to succeed as well.
        var opened = await RecoveryKitStore.ReadAsync(kit, CancellationToken.None);
        using var lease = Bip39RecoveryEnvelope.Unwrap(
            opened.Envelopes.Single(envelope => envelope.Suite == Bip39RecoveryEnvelope.SuiteId),
            provisioned.Repository.Id.ToArray(),
            provisioned.RecoveryMnemonic);

        var adapter = workspace.Adapter("state-verify", new PasswordPipeCredentialProvider(HelperPath, lease));
        Assert.Empty(await adapter.ListSnapshotsAsync(new ListSnapshots(provisioned.Repository), CancellationToken.None));

        // A completed run leaves no intent behind, so a later cleanup has nothing to do.
        Assert.False(File.Exists(Path.Combine(state, IntentFile)));
        Assert.False(await RepositoryProvisioner.CleanUpInterruptedAsync(state, CancellationToken.None));
    }

    [SkippableFact]
    public async Task AnInterruptedRunIsRecognisedAndCleanedUpLater()
    {
        RequireHelper();
        using var workspace = await RecoveryWorkspace.CreateAsync("provision-interrupted", CancellationToken.None);
        var repository = workspace.EnsureDirectory("repository");
        var kit = workspace.EnsureDirectory("kit");
        var state = workspace.EnsureDirectory("state");

        // A process that is killed cannot roll itself back: it leaves the intent and whatever it had
        // created by then.
        await File.WriteAllTextAsync(Path.Combine(repository, "config"), "left behind by a killed run");
        await WriteIntentAsync(state, repository, kit);

        Assert.True(await RepositoryProvisioner.CleanUpInterruptedAsync(state, CancellationToken.None));

        Assert.False(Directory.Exists(repository));
        Assert.False(File.Exists(Path.Combine(state, IntentFile)));
        Assert.False(await RepositoryProvisioner.CleanUpInterruptedAsync(state, CancellationToken.None));
    }

    [SkippableFact]
    public async Task AnUnfinishedRunBlocksAnotherRunInTheSameWorkingDirectory()
    {
        RequireHelper();
        using var workspace = await RecoveryWorkspace.CreateAsync("provision-blocked", CancellationToken.None);
        var state = workspace.EnsureDirectory("state");
        await WriteIntentAsync(state, Path.Combine(workspace.Root, "elsewhere"), Path.Combine(workspace.Root, "elsewhere-kit"));

        var provisioner = new RepositoryProvisioner(RecoveryWorkspace.EngineRootPath, HelperPath);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => provisioner.CreateAsync(
                Path.Combine(workspace.Root, "repository"),
                Path.Combine(workspace.Root, "kit"),
                state,
                CancellationToken.None,
                addDeviceUnlock: false));
    }

    [SkippableFact]
    public async Task AnExistingRepositoryIsNeverAdopted()
    {
        RequireHelper();
        using var workspace = await RecoveryWorkspace.CreateAsync("provision-existing", CancellationToken.None);
        var repository = workspace.EnsureDirectory("repository");
        await File.WriteAllTextAsync(Path.Combine(repository, "config"), "an existing repository");

        var provisioner = new RepositoryProvisioner(RecoveryWorkspace.EngineRootPath, HelperPath);

        // Rollback may only ever undo this run's own work, so a directory with content is refused.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => provisioner.CreateAsync(
                repository,
                Path.Combine(workspace.Root, "kit"),
                workspace.EnsureDirectory("state"),
                CancellationToken.None,
                addDeviceUnlock: false));

        Assert.Equal("an existing repository", await File.ReadAllTextAsync(Path.Combine(repository, "config")));
    }

    private static void RequireHelper() =>
        Skip.IfNot(File.Exists(HelperPath), "The password helper was not built next to the tests.");

    private static Task WriteIntentAsync(string state, string repository, string kit)
    {
        var document = new
        {
            schema = "fortiq.provisioning-intent",
            version = 1,
            repositoryPath = repository,
            kitPath = kit,
            startedAt = DateTimeOffset.UtcNow
        };

        return File.WriteAllTextAsync(Path.Combine(state, IntentFile), JsonSerializer.Serialize(document));
    }
}
