using Fortiq.Desktop.ViewModels;
using Fortiq.Monitoring;

namespace Fortiq.Desktop.Tests;

/// <summary>
/// A failure somebody caused has to stay on the screen until they deal with it. The screen refreshes
/// itself every thirty seconds, and a refresh clears the last failure - which is right for a failure
/// about reading the report and wrong for a backup that was watched failing.
/// </summary>
public sealed class OperationFailureVisibilityTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Fact]
    public async Task AFailedBackupIsStillOnTheScreenAfterThePollRefreshes()
    {
        var model = Model(new FakeBackup(false, "the repository was unreachable"));
        await model.RefreshAsync(CancellationToken.None);
        await model.BackupNowAsync(Assert.Single(model.Repositories), CancellationToken.None);

        await model.RefreshAsync(CancellationToken.None);

        Assert.Equal("the repository was unreachable", model.Failure);
    }

    [Fact]
    public async Task AFailedRecoveryProofIsStillOnTheScreenAfterThePollRefreshes()
    {
        var model = new RepositoriesViewModel(new FixedHealth(Health()), new FakeProof(false));
        await model.RefreshAsync(CancellationToken.None);
        await model.ProveRecoveryAsync(Assert.Single(model.Repositories), CancellationToken.None);

        await model.RefreshAsync(CancellationToken.None);

        Assert.NotNull(model.Failure);
    }

    [Fact]
    public async Task DismissingAFailureLetsTheNextRefreshSpeakForItself()
    {
        var model = Model(new FakeBackup(false, "the repository was unreachable"));
        await model.RefreshAsync(CancellationToken.None);
        await model.BackupNowAsync(Assert.Single(model.Repositories), CancellationToken.None);

        model.ClearFailure();
        await model.RefreshAsync(CancellationToken.None);

        Assert.Null(model.Failure);
    }

    [Fact]
    public async Task StartingAnotherBackupClearsTheFailureFromTheLastOne()
    {
        // Otherwise a successful retry leaves the previous failure standing beside it.
        var backup = new SwitchingBackup(
            new BackupNowResult(false, null, "the repository was unreachable"),
            new BackupNowResult(true, "snapshot-1"));
        var model = Model(backup);
        await model.RefreshAsync(CancellationToken.None);

        await model.BackupNowAsync(Assert.Single(model.Repositories), CancellationToken.None);
        Assert.NotNull(model.Failure);

        await model.BackupNowAsync(Assert.Single(model.Repositories), CancellationToken.None);
        Assert.Null(model.Failure);
    }

    [Fact]
    public async Task AReportThatGoesStaleOutranksAFailureAlreadyOnTheScreen()
    {
        // Both are true, and the one about the machine's current protection is the one that matters:
        // "your last backup failed" is smaller news than "nothing here is verifying protection".
        var clock = new TestClock(Now);
        var model = new RepositoriesViewModel(
            new FixedHealth(Health()),
            new FakeProof(true),
            clock,
            new FakeBackup(false, "the repository was unreachable"));

        await model.RefreshAsync(CancellationToken.None);
        await model.BackupNowAsync(Assert.Single(model.Repositories), CancellationToken.None);

        clock.Now = Now.AddMinutes(6);
        await model.RefreshAsync(CancellationToken.None);

        Assert.Equal(HealthStoreState.Stale, model.State);
        Assert.Contains("older than five minutes", model.Failure!, StringComparison.Ordinal);
    }

    private static RepositoriesViewModel Model(IBackupNow backup) =>
        new(new FixedHealth(Health()), new FakeProof(true), backup: backup);

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

    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FixedHealth(params RepositoryHealth[] repositories) : IHealthSource
    {
        public Task<HealthReadResult> ReadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new HealthReadResult(HealthStoreState.Active, new HealthReport(Now, repositories)));
    }

    private sealed class FakeProof(bool succeeds) : IProveRecovery
    {
        public Task<bool> ProveAsync(string repositoryId, CancellationToken cancellationToken) => Task.FromResult(succeeds);
    }

    private sealed class FakeBackup(bool succeeds, string? failure = null) : IBackupNow
    {
        public Task<BackupNowResult> BackupAsync(string repositoryId, CancellationToken cancellationToken) =>
            Task.FromResult(new BackupNowResult(succeeds, succeeds ? "snapshot-1" : null, failure));
    }

    private sealed class SwitchingBackup(params BackupNowResult[] results) : IBackupNow
    {
        private int _call;

        public Task<BackupNowResult> BackupAsync(string repositoryId, CancellationToken cancellationToken) =>
            Task.FromResult(results[Math.Min(_call++, results.Length - 1)]);
    }
}
