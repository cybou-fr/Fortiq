using System.Runtime.Versioning;
using Fortiq.Application;
using Fortiq.Infrastructure.Keys;
using Fortiq.Provisioning;

namespace Fortiq.Recovery.IntegrationTests;

/// <summary>
/// The device-bound unlock method a provisioned kit gains on a machine with a platform crypto
/// provider: daily work needs no mnemonic, while the mnemonic stays the way back.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DeviceUnlockTests
{
    private static string HelperPath => Path.Combine(AppContext.BaseDirectory, "Fortiq.PasswordHelper.exe");

    [SkippableFact]
    public async Task TheDeviceBacksUpWithoutTheMnemonicWhileTheMnemonicStillRecovers()
    {
        Skip.IfNot(WindowsTpmEnvelope.IsAvailable, "This machine has no platform crypto provider.");
        using var workspace = await RecoveryWorkspace.CreateAsync("device-unlock", CancellationToken.None);
        var source = Path.Combine(workspace.Root, "source");
        var expected = TestDataset.Create(source);

        var kitDirectory = Path.Combine(workspace.Root, "kit");
        var provisioner = new RepositoryProvisioner(RecoveryWorkspace.EngineRootPath, HelperPath);
        var provisioned = await provisioner.CreateAsync(
            workspace.EnsureDirectory("repository"),
            kitDirectory,
            workspace.EnsureDirectory("state-provision"),
            CancellationToken.None);

        var opened = await RecoveryKitStore.ReadAsync(kitDirectory, CancellationToken.None);
        var deviceEnvelope = opened.Envelopes.Single(envelope => envelope.Suite == WindowsTpmEnvelope.SuiteId);
        try
        {
            Assert.True(provisioned.DeviceUnlockAvailable);
            Assert.Equal(2, opened.Manifest.UnlockMethods.Count);

            // A backup on this machine needs the device and nothing else.
            using (var deviceLease = WindowsTpmEnvelope.Unwrap(deviceEnvelope, provisioned.Repository.Id.ToArray()))
            {
                var adapter = workspace.Adapter("state-device", new PasswordPipeCredentialProvider(HelperPath, deviceLease));
                var backup = await adapter.CreateSnapshotAsync(
                    new CreateSnapshot(provisioned.Repository, source, "test-source"),
                    CancellationToken.None);
                Assert.Equal(64, backup.SnapshotId.Length);
            }

            // The recovery envelope in the same kit opens the same repository, so the mnemonic is
            // still a complete way back and both envelopes protect one secret.
            var recoveryEnvelope = opened.Envelopes.Single(envelope => envelope.Suite == Bip39RecoveryEnvelope.SuiteId);
            using var recoveryLease = Bip39RecoveryEnvelope.Unwrap(
                recoveryEnvelope,
                provisioned.Repository.Id.ToArray(),
                provisioned.RecoveryMnemonic);

            var recoveryAdapter = workspace.Adapter("state-recovery", new PasswordPipeCredentialProvider(HelperPath, recoveryLease));
            var snapshots = await recoveryAdapter.ListSnapshotsAsync(new ListSnapshots(provisioned.Repository), CancellationToken.None);
            Assert.Single(snapshots);

            var target = Path.Combine(workspace.Root, "restored");
            await recoveryAdapter.RestoreAsync(
                new RestoreSnapshot(provisioned.Repository, snapshots[0].Id, target, source),
                CancellationToken.None);

            foreach (var entry in expected)
            {
                var file = Path.Combine(target, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                Assert.Equal(entry.Sha256, TestDataset.HashFile(file));
            }
        }
        finally
        {
            WindowsTpmEnvelope.DeleteKey(deviceEnvelope);
        }
    }

    [SkippableFact]
    public async Task LosingTheDeviceKeyLeavesTheRecoveryPathIntact()
    {
        Skip.IfNot(WindowsTpmEnvelope.IsAvailable, "This machine has no platform crypto provider.");
        using var workspace = await RecoveryWorkspace.CreateAsync("device-unlock-lost", CancellationToken.None);
        var kitDirectory = Path.Combine(workspace.Root, "kit");
        var provisioner = new RepositoryProvisioner(RecoveryWorkspace.EngineRootPath, HelperPath);
        var provisioned = await provisioner.CreateAsync(
            workspace.EnsureDirectory("repository"),
            kitDirectory,
            workspace.EnsureDirectory("state-provision"),
            CancellationToken.None);

        var opened = await RecoveryKitStore.ReadAsync(kitDirectory, CancellationToken.None);
        var deviceEnvelope = opened.Envelopes.Single(envelope => envelope.Suite == WindowsTpmEnvelope.SuiteId);

        // The machine loses its key, as it would in a reinstall.
        WindowsTpmEnvelope.DeleteKey(deviceEnvelope);

        // ThrowsAny, not Throws. What matters here is that device unlock fails and the mnemonic still
        // works; which failure it is may be more specific than UnlockFailedException, and is. A
        // deleted key now reports DeviceKeyIdentityException - a subclass - because NCrypt cannot
        // tell "no such key" from "no such key in this scope", so the diagnostic names both. Pinning
        // the exact type made this test fail on any machine with a TPM the moment that improved.
        Assert.ThrowsAny<UnlockFailedException>(
            () => WindowsTpmEnvelope.Unwrap(deviceEnvelope, provisioned.Repository.Id.ToArray()));

        using var recoveryLease = Bip39RecoveryEnvelope.Unwrap(
            opened.Envelopes.Single(envelope => envelope.Suite == Bip39RecoveryEnvelope.SuiteId),
            provisioned.Repository.Id.ToArray(),
            provisioned.RecoveryMnemonic);

        var adapter = workspace.Adapter("state-after-loss", new PasswordPipeCredentialProvider(HelperPath, recoveryLease));
        var check = await adapter.CheckAsync(new CheckRepository(provisioned.Repository), CancellationToken.None);
        Assert.True(check.IsHealthy);
    }
}
