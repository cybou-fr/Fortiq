using System.Text.Json;
using Fortiq.Application;
using Fortiq.Infrastructure.Keys;
using Fortiq.Provisioning;
using Fortiq.Recover;

namespace Fortiq.Recovery.IntegrationTests;

/// <summary>
/// The relations a kit has to satisfy before it is used: it must describe this engine, and once the
/// repository is open, it must be the repository the kit names rather than whatever sits at the path
/// it was pointed at.
/// </summary>
public sealed class KitIntegrityTests
{
    private static string HelperPath => Path.Combine(AppContext.BaseDirectory, "Fortiq.PasswordHelper.exe");

    [SkippableFact]
    public async Task AKitIsRefusedOnADifferentRepositoryThatItsSecretHappensToOpen()
    {
        RequireHelper();
        using var workspace = await RecoveryWorkspace.CreateAsync("kit-wrong-repository", CancellationToken.None);
        var provisioned = await ProvisionAsync(workspace, "first");

        // A second repository initialised with the same engine unlock secret: this is what a reused
        // passphrase or a copy target looks like. The kit's mnemonic opens it, and it is still not
        // the repository the kit describes.
        var opened = await RecoveryKitStore.ReadAsync(provisioned.KitDirectory, CancellationToken.None);
        using var lease = Bip39RecoveryEnvelope.Unwrap(
            opened.Envelopes.Single(envelope => envelope.Suite == Bip39RecoveryEnvelope.SuiteId),
            provisioned.Provisioned.Repository.Id.ToArray(),
            provisioned.Provisioned.RecoveryMnemonic);

        var lookalike = Path.Combine(workspace.Root, "lookalike");
        var adapter = workspace.Adapter("state-lookalike", new PasswordPipeCredentialProvider(HelperPath, lease));
        var second = await adapter.InitializeAsync(new InitializeRepository(lookalike), CancellationToken.None);
        Assert.NotEqual(provisioned.Provisioned.Repository.Id, second.Id);

        var result = await RunRecoverAsync(
            [
                "snapshots",
                "--repository", lookalike,
                "--engine-root", RecoveryWorkspace.EngineRootPath,
                "--kit", provisioned.KitDirectory
            ],
            provisioned.Provisioned.RecoveryMnemonic);

        // It fails as a mismatch rather than as an unlock failure, because those are different facts.
        Assert.Equal(RecoveryCli.ExitKitMismatch, result.ExitCode);
        Assert.Contains("different repository", result.StandardError, StringComparison.Ordinal);
        Assert.Equal(string.Empty, result.StandardOutput.Trim());
    }

    [SkippableFact]
    public async Task AKitOfAnUnrelatedRepositoryStillFailsAsAnUnlockAndTellsNothingMore()
    {
        RequireHelper();
        using var workspace = await RecoveryWorkspace.CreateAsync("kit-unrelated-repository", CancellationToken.None);
        var first = await ProvisionAsync(workspace, "first");
        var second = await ProvisionAsync(workspace, "second");

        var result = await RunRecoverAsync(
            [
                "snapshots",
                "--repository", second.Provisioned.Repository.Location,
                "--engine-root", RecoveryWorkspace.EngineRootPath,
                "--kit", first.KitDirectory
            ],
            first.Provisioned.RecoveryMnemonic);

        // Two repositories with different secrets: the engine cannot even read the identity, so the
        // caller learns only that the unlock failed - not whose repository this is.
        Assert.Equal(RecoveryCli.ExitUnlockFailed, result.ExitCode);
        Assert.Equal("UnlockFailed", result.StandardError.Trim());
        Assert.DoesNotContain(second.Provisioned.Repository.Id.ToString(), result.StandardError, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task AKitWrittenForAnotherEngineIsRefusedBeforeUnlocking()
    {
        RequireHelper();
        using var workspace = await RecoveryWorkspace.CreateAsync("kit-wrong-engine", CancellationToken.None);
        var provisioned = await ProvisionAsync(workspace, "repository");

        // The manifest is rewritten to claim a different engine entirely.
        var manifestPath = Path.Combine(provisioned.KitDirectory, RecoveryKit.ManifestFileName);
        var manifest = await File.ReadAllTextAsync(manifestPath);
        await File.WriteAllTextAsync(manifestPath, manifest.Replace("\"restic\"", "\"borg\"", StringComparison.Ordinal));

        var result = await RunRecoverAsync(
            [
                "snapshots",
                "--repository", provisioned.Provisioned.Repository.Location,
                "--engine-root", RecoveryWorkspace.EngineRootPath,
                "--kit", provisioned.KitDirectory
            ],
            provisioned.Provisioned.RecoveryMnemonic);

        Assert.Equal(RecoveryCli.ExitKitMismatch, result.ExitCode);
        Assert.Contains("different repository engine", result.StandardError, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task AMatchingKitReportsThatTheEngineIsTheOneItWasWrittenWith()
    {
        RequireHelper();
        using var workspace = await RecoveryWorkspace.CreateAsync("kit-engine-agreement", CancellationToken.None);
        var provisioned = await ProvisionAsync(workspace, "repository");

        var result = await RunRecoverAsync(
            [
                "snapshots",
                "--repository", provisioned.Provisioned.Repository.Location,
                "--engine-root", RecoveryWorkspace.EngineRootPath,
                "--kit", provisioned.KitDirectory
            ],
            provisioned.Provisioned.RecoveryMnemonic);

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal("Identical", document.RootElement.GetProperty("engineAgreement").GetString());
    }

    [Fact]
    public void ADifferentBuildOfTheSameEngineIsAllowedAndReported()
    {
        var kit = new RecoveryKit(
            new string('a', 64),
            "C:/repository",
            new RecoveryKitEngine("restic", "0.19.1", new string('b', 64)),
            DateTimeOffset.UnixEpoch,
            [],
            "instructions");

        // Refusing an upgraded engine would make a kit brittle exactly when recovery matters, so a
        // different build of the same engine is reported rather than rejected.
        Assert.Equal(EngineAgreement.Identical, RecoveryKitPolicy.CompareEngine(kit, "restic", "0.19.1", new string('b', 64)));
        Assert.Equal(EngineAgreement.DifferentBuild, RecoveryKitPolicy.CompareEngine(kit, "restic", "0.20.0", new string('c', 64)));
        Assert.Throws<RecoveryKitMismatchException>(() => RecoveryKitPolicy.CompareEngine(kit, "borg", "1.4.0", new string('b', 64)));
    }

    [Fact]
    public void TheRepositoryComparisonIsOnStatedIdentityNotOnPath()
    {
        var identity = Convert.FromHexString(new string('a', 64));
        var kit = new RecoveryKit(
            Convert.ToHexStringLower(identity),
            "C:/somewhere-else",
            new RecoveryKitEngine("restic", "0.19.1", new string('b', 64)),
            DateTimeOffset.UnixEpoch,
            [],
            "instructions");

        // The locator in the manifest says nothing about which repository this is.
        RecoveryKitPolicy.RequireSameRepository(kit, identity);

        var other = identity.ToArray();
        other[0] ^= 0x01;
        Assert.Throws<RecoveryKitMismatchException>(() => RecoveryKitPolicy.RequireSameRepository(kit, other));
    }

    private static void RequireHelper() =>
        Skip.IfNot(File.Exists(HelperPath), "The password helper was not built next to the tests.");

    private static async Task<(ProvisionedRepository Provisioned, string KitDirectory)> ProvisionAsync(
        RecoveryWorkspace workspace,
        string name)
    {
        var kitDirectory = Path.Combine(workspace.Root, $"{name}-kit");
        var provisioner = new RepositoryProvisioner(RecoveryWorkspace.EngineRootPath, HelperPath);
        var provisioned = await provisioner.CreateAsync(
            Path.Combine(workspace.Root, name),
            kitDirectory,
            workspace.EnsureDirectory($"{name}-state"),
            CancellationToken.None,
            addDeviceUnlock: false);

        return (provisioned, kitDirectory);
    }

    private static Task<RecoveryToolResult> RunRecoverAsync(string[] arguments, string mnemonic) =>
        RecoveryTool.RunAsync(arguments, mnemonic);
}
