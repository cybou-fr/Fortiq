using System.Runtime.Versioning;
using Fortiq.Application;
using Fortiq.Domain;
using Fortiq.Infrastructure.Keys;
using Fortiq.Infrastructure.Runs;
using Fortiq.Monitoring;
using Fortiq.Operations;
using Fortiq.Provisioning;
using Fortiq.Scheduling;

namespace Fortiq.Recovery.IntegrationTests;

/// <summary>
/// Clearing the lock an interrupted run leaves behind.
/// </summary>
/// <remarks>
/// The engine has been able to do this all along and nothing in the product ever asked it to, so the
/// first thing worth proving is that the composition works at all against the real binary: the engine
/// is verified, the kit opens the repository, the repository is the one the kit describes, and a
/// receipt comes out. A feature that is quietly inert looks exactly like one that works until the day
/// somebody needs it - which, for this one, is the day their backups have already stopped.
///
/// The second thing worth proving is the guard. Clearing removes a lock whose owner cannot be proven
/// dead, so it must never run while one of this machine's own operations holds the repository.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class StaleLockRecoveryTests
{
    private static string HelperPath => Path.Combine(AppContext.BaseDirectory, "Fortiq.PasswordHelper.exe");

    [SkippableFact]
    public async Task ClearingLocksRunsAgainstTheRealRepositoryAndLeavesEvidence()
    {
        Skip.IfNot(WindowsTpmEnvelope.IsAvailable, "This machine has no platform crypto provider.");
        Skip.IfNot(File.Exists(HelperPath), "The password helper was not built next to the tests.");

        using var workspace = await RecoveryWorkspace.CreateAsync("stale-lock", CancellationToken.None);
        var (schedule, receipts, runs) = await ProtectedSourceAsync(workspace);

        await Recovery(workspace, receipts, runs).ClearAsync(schedule, CancellationToken.None);

        // The repository is still usable afterwards - clearing locks must not be something that costs
        // a repository - and the operation left a record of itself.
        var history = await ReceiptTimeline.ReadAsync(receipts, CancellationToken.None);
        Assert.Contains(history, entry => entry.Operation == "reconcile" && entry.Succeeded);

        var backup = Backup(workspace, receipts, runs);
        var afterwards = await backup.RunAsync(schedule, CancellationToken.None);
        Assert.NotNull(afterwards.SnapshotId);
    }

    [SkippableFact]
    public async Task ClearingIsRefusedWhileOneOfThisMachinesOwnRunsHoldsTheRepository()
    {
        Skip.IfNot(WindowsTpmEnvelope.IsAvailable, "This machine has no platform crypto provider.");
        Skip.IfNot(File.Exists(HelperPath), "The password helper was not built next to the tests.");

        using var workspace = await RecoveryWorkspace.CreateAsync("stale-lock-busy", CancellationToken.None);
        var (schedule, receipts, runs) = await ProtectedSourceAsync(workspace);

        var kit = await RecoveryKitStore.ReadAsync(schedule.KitDirectory, CancellationToken.None);
        var repository = RepositoryId.FromBytes(Convert.FromHexString(kit.Manifest.RepositoryId));

        // A run of this machine's, held open across the attempt - the situation that makes clearing
        // dangerous, because the lock it would remove belongs to the operation still using it.
        var holder = new FileSystemRepositoryRunRegistry(runs, TimeSpan.FromMilliseconds(200));
        await using var held = await holder.BeginAsync(
            repository,
            OperationKind.Backup,
            Guid.NewGuid(),
            RunExclusivity.Shared,
            CancellationToken.None);

        await Assert.ThrowsAsync<RepositoryBusyException>(
            () => Recovery(workspace, receipts, runs).ClearAsync(schedule, CancellationToken.None));
    }

    private static async Task<(BackupSchedule Schedule, string Receipts, string Runs)> ProtectedSourceAsync(RecoveryWorkspace workspace)
    {
        var source = Path.Combine(workspace.Root, "source");
        TestDataset.Create(source);

        var kitDirectory = Path.Combine(workspace.Root, "kit");
        var provisioned = await new RepositoryProvisioner(RecoveryWorkspace.EngineRootPath, HelperPath).CreateAsync(
            workspace.EnsureDirectory("repository"),
            kitDirectory,
            workspace.EnsureDirectory("state-provision"),
            CancellationToken.None);

        var receipts = workspace.EnsureDirectory("receipts");
        var runs = workspace.EnsureDirectory("runs");
        var schedule = new BackupSchedule(
            "documents",
            provisioned.Repository.Location,
            kitDirectory,
            source,
            "workstation:documents",
            new EveryInterval(TimeSpan.FromHours(6)));

        // Something has to be in the repository before clearing its locks means anything.
        await Backup(workspace, receipts, runs).RunAsync(schedule, CancellationToken.None);

        return (schedule, receipts, runs);
    }

    private static UnattendedBackup Backup(RecoveryWorkspace workspace, string receipts, string runs) =>
        new(RecoveryWorkspace.EngineRootPath, workspace.EnsureDirectory("backup-work"), HelperPath, runs, receipts);

    private static StaleLockRecovery Recovery(RecoveryWorkspace workspace, string receipts, string runs) =>
        new(RecoveryWorkspace.EngineRootPath, workspace.EnsureDirectory("lock-work"), HelperPath, runs, receipts);
}
