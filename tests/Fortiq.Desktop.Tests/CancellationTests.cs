using Fortiq.Desktop.ViewModels;
using Fortiq.Monitoring;

namespace Fortiq.Desktop.Tests;

/// <summary>
/// Stopping a backup that is running. A backup of a large folder takes minutes, and until this
/// existed the only way out was to close the window and let the work finish invisibly.
/// </summary>
public sealed class CancellationTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Fact]
    public void NothingCanBeStoppedWhileNothingIsRunning()
    {
        var model = new RepositoriesViewModel(new FixedHealth(Health()), new FakeProof(true), backup: new BlockingBackup());

        Assert.False(model.CanCancel);
        model.CancelRunning();
        Assert.Null(model.Failure);
    }

    [Fact]
    public async Task StoppingABackupReachesTheOperationAndIsReportedAsWhatHappened()
    {
        var backup = new BlockingBackup();
        var model = new RepositoriesViewModel(new FixedHealth(Health()), new FakeProof(true), backup: backup);
        await model.RefreshAsync(CancellationToken.None);

        var running = model.BackupNowAsync(Assert.Single(model.Repositories), CancellationToken.None);
        await backup.Started;

        Assert.True(model.CanCancel);
        model.CancelRunning();
        await running;

        Assert.True(backup.Observed, "the operation was never told to stop");
        Assert.False(model.CanCancel);
        Assert.Contains("stopped before it finished", model.Failure!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStoppedBackupSaysWhatItMayHaveLeftBehind()
    {
        // Cancellation kills the engine, and a killed run leaves its lock in the repository. Somebody
        // who stops a backup and then finds every later one failing has to be able to connect the two.
        var backup = new BlockingBackup();
        var model = new RepositoriesViewModel(new FixedHealth(Health()), new FakeProof(true), backup: backup);
        await model.RefreshAsync(CancellationToken.None);

        var running = model.BackupNowAsync(Assert.Single(model.Repositories), CancellationToken.None);
        await backup.Started;
        model.CancelRunning();
        await running;

        Assert.Contains("locked", model.Failure!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACancellationTheCallerAskedForIsNotReportedAsSomethingSomebodyDid()
    {
        // The window closing takes the whole screen down with it; reporting "you stopped this" to a
        // screen that is going away is noise, and the token it came from is not the person's.
        var backup = new BlockingBackup();
        var model = new RepositoriesViewModel(new FixedHealth(Health()), new FakeProof(true), backup: backup);
        await model.RefreshAsync(CancellationToken.None);

        using var caller = new CancellationTokenSource();
        var running = model.BackupNowAsync(Assert.Single(model.Repositories), caller.Token);
        await backup.Started;
        await caller.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
    }

    private static RepositoryHealth Health() => HealthAssessor.Assess(
        new RepositoryFacts(
            "a",
            "documents",
            LastBackupAt: Now.AddHours(-1),
            LastHealthyCheckAt: null,
            LastProvenRestoreAt: null,
            KitPresent: true,
            StorageImmutable: true,
            StorageProtectionNow: StorageProtectionStatus.Immutable),
        Now);

    private sealed class FixedHealth(params RepositoryHealth[] repositories) : IHealthSource
    {
        public Task<HealthReadResult> ReadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new HealthReadResult(HealthStoreState.Active, new HealthReport(Now, repositories)));
    }

    private sealed class FakeProof(bool succeeds) : IProveRecovery
    {
        public Task<bool> ProveAsync(string repositoryId, CancellationToken cancellationToken) => Task.FromResult(succeeds);
    }

    /// <summary>A backup that runs until it is told not to, so cancelling it means something.</summary>
    private sealed class BlockingBackup : IBackupNow
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task Started => _started.Task;

        internal bool Observed { get; private set; }

        public async Task<BackupNowResult> BackupAsync(string repositoryId, CancellationToken cancellationToken)
        {
            _started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Observed = true;
                throw;
            }

            return new BackupNowResult(true, "snapshot-1");
        }
    }
}
