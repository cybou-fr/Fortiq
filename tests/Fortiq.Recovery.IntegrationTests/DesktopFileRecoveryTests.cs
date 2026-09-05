using Fortiq.Application;
using Fortiq.Desktop;
using Fortiq.Desktop.ViewModels;
using Fortiq.Infrastructure.Keys;
using Fortiq.Provisioning;

namespace Fortiq.Recovery.IntegrationTests;

public sealed class DesktopFileRecoveryTests
{
    [SkippableFact]
    public async Task GuiRecoveryBackendRestoresTheChosenBackupWithoutDeviceUnlock()
    {
        var helper = Path.Combine(AppContext.BaseDirectory, "Fortiq.PasswordHelper.exe");
        Skip.IfNot(File.Exists(helper), "The password helper is unavailable.");
        using var workspace = await RecoveryWorkspace.CreateAsync("desktop-file-recovery", CancellationToken.None);
        var source = workspace.EnsureDirectory("source");
        var expected = TestDataset.Create(source);
        var kitPath = Path.Combine(workspace.Root, "kit");
        var repositoryPath = Path.Combine(workspace.Root, "repository");
        var provisioned = await new RepositoryProvisioner(RecoveryWorkspace.EngineRootPath, helper).CreateAsync(
            repositoryPath, kitPath, workspace.EnsureDirectory("provision"), CancellationToken.None, addDeviceUnlock: false);
        var kit = await RecoveryKitStore.ReadAsync(kitPath, CancellationToken.None);
        using var lease = Bip39RecoveryEnvelope.Unwrap(
            kit.Envelopes.Single(item => item.Suite == Bip39RecoveryEnvelope.SuiteId),
            provisioned.Repository.Id.ToArray(), provisioned.RecoveryMnemonic);
        using var credentials = new PasswordPipeCredentialProvider(helper, lease);
        var backup = await workspace.Adapter("backup", credentials).CreateSnapshotAsync(
            new CreateSnapshot(provisioned.Repository, source, "desktop-test"), CancellationToken.None);

        var model = new FileRecoveryViewModel(new FileRecoveryAdapter(RecoveryWorkspace.EngineRootPath, workspace.EnsureDirectory("runs")));
        await model.LoadAsync(new FileRecoveryAccess(repositoryPath, kitPath, provisioned.RecoveryMnemonic));
        var snapshot = Assert.Single(model.Snapshots);
        Assert.Equal(backup.SnapshotId, snapshot.Id);
        var target = Path.Combine(workspace.Root, "restored");
        await model.RestoreAsync(snapshot, target);
        Assert.True(model.Completed, model.Status);
        foreach (var entry in expected)
        {
            var restored = Path.Combine(target, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(restored), entry.RelativePath);
            Assert.Equal(entry.Sha256, TestDataset.HashFile(restored));
        }
        Assert.Equal(expected.Count, Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories).Count());
    }
}
