using Fortiq.Application;
using Fortiq.Infrastructure.Runs;
using Fortiq.Provisioning;
using Fortiq.Recover;

namespace Fortiq.Recovery.IntegrationTests;

/// <summary>
/// Runs are registered across processes, which is the point: the recovery tool is a separate program
/// from whatever else may be working on the repository at the time.
/// </summary>
public sealed class CrossProcessRunTests
{
    private static string HelperPath => Path.Combine(AppContext.BaseDirectory, "Fortiq.PasswordHelper.exe");

    [SkippableFact]
    public async Task TheRecoveryToolWaitsForARepositoryAnotherRunHoldsExclusively()
    {
        Skip.IfNot(File.Exists(HelperPath), "The password helper was not built next to the tests.");
        using var workspace = await RecoveryWorkspace.CreateAsync("cross-process-run", CancellationToken.None);

        var kitDirectory = Path.Combine(workspace.Root, "kit");
        var provisioner = new RepositoryProvisioner(RecoveryWorkspace.EngineRootPath, HelperPath);
        var provisioned = await provisioner.CreateAsync(
            Path.Combine(workspace.Root, "repository"),
            kitDirectory,
            workspace.EnsureDirectory("state"),
            CancellationToken.None,
            addDeviceUnlock: false);

        string[] arguments =
        [
            "snapshots",
            "--repository", provisioned.Repository.Location,
            "--engine-root", RecoveryWorkspace.EngineRootPath,
            "--kit", kitDirectory
        ];

        // This process claims the repository the way a reconciliation would, in the same registry the
        // recovery tool reads.
        var registry = new FileSystemRepositoryRunRegistry(FortiqRunDirectory.Default(), TimeSpan.FromMilliseconds(200));
        var blocked = await registry.BeginAsync(
            provisioned.Repository.Id,
            OperationKind.Reconcile,
            Guid.NewGuid(),
            RunExclusivity.Exclusive,
            CancellationToken.None);

        RecoveryToolResult held;
        try
        {
            held = await RecoveryTool.RunAsync(arguments, provisioned.RecoveryMnemonic);
        }
        finally
        {
            await blocked.DisposeAsync();
        }

        Assert.Equal(RecoveryCli.ExitRepositoryBusy, held.ExitCode);
        Assert.Contains("another Fortiq run", held.StandardError, StringComparison.OrdinalIgnoreCase);

        // Once the exclusive run ends, the same command succeeds without any other change.
        var free = await RecoveryTool.RunAsync(arguments, provisioned.RecoveryMnemonic);
        Assert.Equal(RecoveryCli.ExitSuccess, free.ExitCode);
    }
}
