using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Fortiq.Application;
using Fortiq.Infrastructure.Keys;
using Fortiq.Recover;

namespace Fortiq.Recovery.IntegrationTests;

/// <summary>
/// E2E-001 in its full form: after the local Fortiq state is destroyed, a separate
/// <c>Fortiq.Recover</c> process opens the repository with nothing but the recovery kit and restores
/// the dataset. The mnemonic is typed on standard input; it never appears in a process argument.
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
        var repository = workspace.EnsureDirectory("repository");
        var expected = TestDataset.Create(source);

        var engineUnlockSecret = RandomNumberGenerator.GetBytes(32);
        var mnemonic = Bip39Mnemonic.Create();
        using var lease = new BufferKeyLease(engineUnlockSecret);

        var backupState = workspace.EnsureDirectory("state-backup");
        var adapter = workspace.Adapter("state-backup", new PasswordPipeCredentialProvider(HelperPath, lease));
        var descriptor = await adapter.InitializeAsync(new InitializeRepository(repository), CancellationToken.None);
        var backup = await adapter.CreateSnapshotAsync(new CreateSnapshot(descriptor, source, "test-source"), CancellationToken.None);

        // The recovery kit is written once the repository exists, and holds only the wrapped secret.
        var envelopePath = Path.Combine(workspace.EnsureDirectory("kit"), "engine-unlock.cbor");
        var envelope = Bip39RecoveryEnvelope.Wrap(descriptor.Id.ToArray(), mnemonic, lease);
        await File.WriteAllBytesAsync(envelopePath, KeyEnvelopeCodec.Encode(envelope));

        // Everything Fortiq kept outside the repository and the kit is destroyed.
        Directory.Delete(backupState, recursive: true);

        var listed = await RunRecoverAsync(
            ["snapshots", "--repository", repository, "--engine-root", RecoveryWorkspace.EngineRootPath, "--envelope", envelopePath],
            mnemonic);
        Assert.Equal(0, listed.ExitCode);
        using (var document = JsonDocument.Parse(listed.StandardOutput))
        {
            var snapshots = document.RootElement.GetProperty("snapshots").EnumerateArray().ToArray();
            Assert.Contains(snapshots, snapshot => snapshot.GetProperty("id").GetString() == backup.SnapshotId);
        }

        var checkResult = await RunRecoverAsync(
            ["check", "--repository", repository, "--engine-root", RecoveryWorkspace.EngineRootPath, "--envelope", envelopePath],
            mnemonic);
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
                "--envelope", envelopePath,
                "--snapshot", backup.SnapshotId,
                "--target", target,
                "--source", source
            ],
            mnemonic);

        Assert.Equal(0, restored.ExitCode);
        foreach (var entry in expected)
        {
            var file = Path.Combine(target, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(file), $"Missing restored file: {entry.RelativePath}");
            Assert.Equal(entry.Sha256, TestDataset.HashFile(file));
        }

        // Neither the mnemonic nor the engine password may appear in what the tool printed.
        var printed = restored.StandardOutput + restored.StandardError;
        Assert.DoesNotContain(mnemonic, printed, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Convert.ToHexStringLower(engineUnlockSecret), printed.ToLowerInvariant(), StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task AWrongMnemonicFailsAsUnlockFailedWithoutRevealingSnapshots()
    {
        using var workspace = await RecoveryWorkspace.CreateAsync("standalone-wrong-secret", CancellationToken.None);
        var source = Path.Combine(workspace.Root, "source");
        var repository = workspace.EnsureDirectory("repository");
        TestDataset.Create(source);

        using var lease = new BufferKeyLease(RandomNumberGenerator.GetBytes(32));
        var mnemonic = Bip39Mnemonic.Create();
        var adapter = workspace.Adapter("state", new PasswordPipeCredentialProvider(HelperPath, lease));
        var descriptor = await adapter.InitializeAsync(new InitializeRepository(repository), CancellationToken.None);
        var backup = await adapter.CreateSnapshotAsync(new CreateSnapshot(descriptor, source, "test-source"), CancellationToken.None);

        var envelopePath = Path.Combine(workspace.EnsureDirectory("kit"), "engine-unlock.cbor");
        await File.WriteAllBytesAsync(
            envelopePath,
            KeyEnvelopeCodec.Encode(Bip39RecoveryEnvelope.Wrap(descriptor.Id.ToArray(), mnemonic, lease)));

        var result = await RunRecoverAsync(
            ["snapshots", "--repository", repository, "--engine-root", RecoveryWorkspace.EngineRootPath, "--envelope", envelopePath],
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
        var source = Path.Combine(workspace.Root, "source");
        var repository = workspace.EnsureDirectory("repository");
        TestDataset.Create(source);

        using var lease = new BufferKeyLease(RandomNumberGenerator.GetBytes(32));
        var mnemonic = Bip39Mnemonic.Create();
        var adapter = workspace.Adapter("state", new PasswordPipeCredentialProvider(HelperPath, lease));
        var descriptor = await adapter.InitializeAsync(new InitializeRepository(repository), CancellationToken.None);

        var envelopePath = Path.Combine(workspace.EnsureDirectory("kit"), "engine-unlock.cbor");
        await File.WriteAllBytesAsync(
            envelopePath,
            KeyEnvelopeCodec.Encode(Bip39RecoveryEnvelope.Wrap(descriptor.Id.ToArray(), mnemonic, lease)));

        // No mnemonic is offered on standard input at all.
        var result = await RunRecoverAsync(
            ["inspect", "--repository", repository, "--engine-root", RecoveryWorkspace.EngineRootPath, "--envelope", envelopePath],
            mnemonic: null);

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.StandardOutput);
        var kit = document.RootElement.GetProperty("envelope");
        Assert.True(document.RootElement.GetProperty("repositoryPresent").GetBoolean());
        Assert.Equal(Bip39RecoveryEnvelope.SuiteId, kit.GetProperty("suite").GetString());
        Assert.True(kit.GetProperty("supported").GetBoolean());
        Assert.Equal(descriptor.Id.ToString().ToLowerInvariant(), kit.GetProperty("repositoryId").GetString());
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
