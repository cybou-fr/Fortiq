using System.Diagnostics;
using System.Text.Json;
using Fortiq.Application;
using Fortiq.Infrastructure.Keys;
using Fortiq.Provisioning;
using Fortiq.Recover;

namespace Fortiq.Recovery.IntegrationTests;

/// <summary>
/// E2E-001 in its full form: a repository and its kit are created by product code, the local Fortiq
/// state is destroyed, and a separate <c>Fortiq.Recover</c> process restores the dataset from the kit
/// alone. The mnemonic is typed on standard input; it never appears in a process argument.
/// </summary>
public sealed class StandaloneRecoveryTests
{
    private static string RecoverPath => Path.Combine(AppContext.BaseDirectory, "Fortiq.Recover.exe");
    private static string HelperPath => Path.Combine(AppContext.BaseDirectory, "Fortiq.PasswordHelper.exe");

    [SkippableFact]
    public async Task ASeparateProcessRestoresTheDatasetFromTheRecoveryKitAlone()
    {
        using var workspace = await RecoveryWorkspace.CreateAsync("standalone-recovery", CancellationToken.None);
        var source = Path.Combine(workspace.Root, "source");
        var expected = TestDataset.Create(source);

        var provisioned = await ProvisionAsync(workspace);
        using var lease = UnlockKit(workspace, provisioned);

        var backupState = workspace.EnsureDirectory("state-backup");
        var adapter = workspace.Adapter("state-backup", new PasswordPipeCredentialProvider(HelperPath, lease));
        var backup = await adapter.CreateSnapshotAsync(
            new CreateSnapshot(provisioned.Repository, source, "test-source"),
            CancellationToken.None);

        // Everything Fortiq kept outside the repository and the kit is destroyed.
        Directory.Delete(backupState, recursive: true);

        var kit = KitDirectory(workspace);
        var repository = provisioned.Repository.Location;

        var listed = await RunRecoverAsync(
            ["snapshots", "--repository", repository, "--engine-root", RecoveryWorkspace.EngineRootPath, "--kit", kit],
            provisioned.RecoveryMnemonic);
        Assert.Equal(0, listed.ExitCode);
        using (var document = JsonDocument.Parse(listed.StandardOutput))
        {
            var snapshots = document.RootElement.GetProperty("snapshots").EnumerateArray().ToArray();
            Assert.Contains(snapshots, snapshot => snapshot.GetProperty("id").GetString() == backup.SnapshotId);
        }

        var checkResult = await RunRecoverAsync(
            ["check", "--repository", repository, "--engine-root", RecoveryWorkspace.EngineRootPath, "--kit", kit],
            provisioned.RecoveryMnemonic);
        Assert.Equal(0, checkResult.ExitCode);
        using (var document = JsonDocument.Parse(checkResult.StandardOutput))
        {
            Assert.True(document.RootElement.GetProperty("healthy").GetBoolean());
        }

        var target = Path.Combine(workspace.Root, "restored");
        var restored = await RunRecoverAsync(
            [
                "restore",
                "--repository", repository,
                "--engine-root", RecoveryWorkspace.EngineRootPath,
                "--kit", kit,
                "--snapshot", backup.SnapshotId,
                "--target", target,
                "--source", source
            ],
            provisioned.RecoveryMnemonic);

        Assert.Equal(0, restored.ExitCode);
        foreach (var entry in expected)
        {
            var file = Path.Combine(target, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(file), $"Missing restored file: {entry.RelativePath}");
            Assert.Equal(entry.Sha256, TestDataset.HashFile(file));
        }

        // The mnemonic may not appear in anything the tool printed, and the kit must not contain it.
        var printed = restored.StandardOutput + restored.StandardError;
        Assert.DoesNotContain(provisioned.RecoveryMnemonic, printed, StringComparison.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(kit))
        {
            var content = await File.ReadAllTextAsync(file);
            Assert.DoesNotContain(provisioned.RecoveryMnemonic, content, StringComparison.OrdinalIgnoreCase);
        }
    }

    [SkippableFact]
    public async Task TheSourceIdentityComesFromTheRepositoryAndNotFromLocalEvidence()
    {
        using var workspace = await RecoveryWorkspace.CreateAsync("standalone-metadata", CancellationToken.None);
        var source = Path.Combine(workspace.Root, "source");
        TestDataset.Create(source);

        var provisioned = await ProvisionAsync(workspace);
        using var lease = UnlockKit(workspace, provisioned);

        var adapter = workspace.RecordingAdapter("state", new PasswordPipeCredentialProvider(HelperPath, lease));
        var backup = await adapter.CreateSnapshotAsync(
            new CreateSnapshot(provisioned.Repository, source, "workstation:documents"),
            CancellationToken.None);

        // Every receipt and everything else Fortiq kept locally is destroyed. The identity of the
        // source has to come out of the repository itself.
        Directory.Delete(workspace.ReceiptDirectory, recursive: true);
        Directory.Delete(workspace.EnsureDirectory("state"), recursive: true);

        var listed = await RunRecoverAsync(
            [
                "snapshots",
                "--repository", provisioned.Repository.Location,
                "--engine-root", RecoveryWorkspace.EngineRootPath,
                "--kit", KitDirectory(workspace)
            ],
            provisioned.RecoveryMnemonic);

        Assert.Equal(0, listed.ExitCode);
        using var document = JsonDocument.Parse(listed.StandardOutput);
        var snapshot = document.RootElement
            .GetProperty("snapshots")
            .EnumerateArray()
            .Single(candidate => candidate.GetProperty("id").GetString() == backup.SnapshotId);

        Assert.Equal("workstation:documents", snapshot.GetProperty("source").GetString());
        Assert.Equal(source, snapshot.GetProperty("path").GetString());
    }

    [SkippableFact]
    public async Task AWrongMnemonicFailsAsUnlockFailedWithoutRevealingSnapshots()
    {
        using var workspace = await RecoveryWorkspace.CreateAsync("standalone-wrong-secret", CancellationToken.None);
        var source = Path.Combine(workspace.Root, "source");
        TestDataset.Create(source);

        var provisioned = await ProvisionAsync(workspace);
        using var lease = UnlockKit(workspace, provisioned);
        var adapter = workspace.Adapter("state", new PasswordPipeCredentialProvider(HelperPath, lease));
        var backup = await adapter.CreateSnapshotAsync(
            new CreateSnapshot(provisioned.Repository, source, "test-source"),
            CancellationToken.None);

        var result = await RunRecoverAsync(
            [
                "snapshots",
                "--repository", provisioned.Repository.Location,
                "--engine-root", RecoveryWorkspace.EngineRootPath,
                "--kit", KitDirectory(workspace)
            ],
            Bip39Mnemonic.Create());

        Assert.Equal(RecoveryCli.ExitUnlockFailed, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardOutput.Trim());
        Assert.DoesNotContain(backup.SnapshotId, result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("UnlockFailed", result.StandardError, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task InspectDescribesTheKitWithoutAskingForRecoveryMaterial()
    {
        using var workspace = await RecoveryWorkspace.CreateAsync("standalone-inspect", CancellationToken.None);
        var provisioned = await ProvisionAsync(workspace);

        // No mnemonic is offered on standard input at all.
        var result = await RunRecoverAsync(
            [
                "inspect",
                "--repository", provisioned.Repository.Location,
                "--engine-root", RecoveryWorkspace.EngineRootPath,
                "--kit", KitDirectory(workspace)
            ],
            mnemonic: null);

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.StandardOutput);
        var kit = document.RootElement.GetProperty("kit");
        Assert.True(document.RootElement.GetProperty("repositoryPresent").GetBoolean());
        Assert.Equal(provisioned.Repository.Id.ToString().ToLowerInvariant(), kit.GetProperty("repositoryId").GetString());

        var method = Assert.Single(kit.GetProperty("unlockMethods").EnumerateArray());
        Assert.Equal("bip39", method.GetProperty("providerType").GetString());
        Assert.Equal(Bip39RecoveryEnvelope.SuiteId, method.GetProperty("suite").GetString());
        Assert.True(method.GetProperty("supported").GetBoolean());
    }

    [SkippableFact]
    public async Task ATamperedKitIsRefusedBeforeAnyUnlockIsAttempted()
    {
        using var workspace = await RecoveryWorkspace.CreateAsync("standalone-tampered-kit", CancellationToken.None);
        var provisioned = await ProvisionAsync(workspace);
        var kit = KitDirectory(workspace);

        var envelopeFile = Directory.EnumerateFiles(kit, "*.cbor").Single();
        var bytes = await File.ReadAllBytesAsync(envelopeFile);
        bytes[^1] ^= 0x01;
        await File.WriteAllBytesAsync(envelopeFile, bytes);

        var result = await RunRecoverAsync(
            [
                "inspect",
                "--repository", provisioned.Repository.Location,
                "--engine-root", RecoveryWorkspace.EngineRootPath,
                "--kit", kit
            ],
            mnemonic: null);

        Assert.Equal(RecoveryCli.ExitDataError, result.ExitCode);
        Assert.Contains("does not match the hash", result.StandardError, StringComparison.Ordinal);
    }

    private static string KitDirectory(RecoveryWorkspace workspace) => Path.Combine(workspace.Root, "kit");

    private static async Task<ProvisionedRepository> ProvisionAsync(RecoveryWorkspace workspace)
    {
        Skip.IfNot(File.Exists(HelperPath), "The password helper was not built next to the tests.");

        var provisioner = new RepositoryProvisioner(RecoveryWorkspace.EngineRootPath, HelperPath);

        // These tests are about the recovery path, so they provision without a device-bound method:
        // it would leave a TPM key behind and says nothing about recovering on another machine.
        return await provisioner.CreateAsync(
            workspace.EnsureDirectory("repository"),
            KitDirectory(workspace),
            workspace.EnsureDirectory("state-provision"),
            CancellationToken.None,
            addDeviceUnlock: false);
    }

    /// <summary>Opens the kit the way any later run has to: with the mnemonic and nothing else.</summary>
    private static IKeyLease UnlockKit(RecoveryWorkspace workspace, ProvisionedRepository provisioned)
    {
        var opened = RecoveryKitStore.ReadAsync(KitDirectory(workspace), CancellationToken.None).GetAwaiter().GetResult();
        return Bip39RecoveryEnvelope.Unwrap(
            opened.Envelopes[0],
            provisioned.Repository.Id.ToArray(),
            provisioned.RecoveryMnemonic);
    }

    private static async Task<ProcessResult> RunRecoverAsync(string[] arguments, string? mnemonic)
    {
        Skip.IfNot(File.Exists(RecoverPath), "The recovery tool was not built next to the tests.");

        var startInfo = new ProcessStartInfo
        {
            FileName = RecoverPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start the recovery tool.");
        if (mnemonic is not null)
        {
            await process.StandardInput.WriteLineAsync(mnemonic);
        }

        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return new ProcessResult(process.ExitCode, await stdout, await stderr);
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
