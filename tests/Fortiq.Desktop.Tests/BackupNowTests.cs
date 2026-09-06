using Fortiq.Desktop.ViewModels;
using Fortiq.Monitoring;

namespace Fortiq.Desktop.Tests;

/// <summary>
/// Backing a source up because somebody asked. Until this existed the application could show the
/// state of the machine and change nothing about it: on a PC with no device key, or in portable mode,
/// a repository was provisioned and then never written to again.
/// </summary>
public sealed class BackupNowTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Fact]
    public async Task ABackupRefreshesWhatTheScreenClaims()
    {
        var health = new SwitchingHealth(
            Health(HealthVerdict.AtRisk, "backup-overdue"),
            Health(HealthVerdict.Unproven));

        var model = new RepositoriesViewModel(health, new FakeProof(true), backup: new FakeBackup(true));
        await model.RefreshAsync(CancellationToken.None);

        await model.BackupNowAsync(Assert.Single(model.Repositories), CancellationToken.None);

        Assert.Null(model.Failure);
        Assert.Equal("Backed up, but recovery has not been proven.", Assert.Single(model.Repositories).Summary);
    }

    [Fact]
    public async Task AFailedBackupSurvivesTheRefreshThatFollowsIt()
    {
        // The refresh comes after the attempt, and it clears Failure. An outcome cleared by it would
        // leave somebody looking at a calm screen believing a backup they watched fail had worked.
        var model = new RepositoriesViewModel(
            new FixedHealth(Health(HealthVerdict.Unproven)),
            new FakeProof(true),
            backup: new FakeBackup(false, "the repository was unreachable"));

        await model.RefreshAsync(CancellationToken.None);
        await model.BackupNowAsync(Assert.Single(model.Repositories), CancellationToken.None);

        Assert.Equal("the repository was unreachable", model.Failure);
    }

    [Fact]
    public async Task ABackupThatFailedWithoutSayingWhyStillReportsAFailure()
    {
        var model = new RepositoriesViewModel(
            new FixedHealth(Health(HealthVerdict.Unproven)),
            new FakeProof(true),
            backup: new FakeBackup(false));

        await model.RefreshAsync(CancellationToken.None);
        await model.BackupNowAsync(Assert.Single(model.Repositories), CancellationToken.None);

        Assert.NotNull(model.Failure);
    }

    [Fact]
    public async Task AThrownFailureIsShownInWordsRatherThanAsAnException()
    {
        var model = new RepositoriesViewModel(
            new FixedHealth(Health(HealthVerdict.Unproven)),
            new FakeProof(true),
            backup: new ThrowingBackup());

        await model.RefreshAsync(CancellationToken.None);
        await model.BackupNowAsync(Assert.Single(model.Repositories), CancellationToken.None);

        Assert.NotNull(model.Failure);
        Assert.DoesNotContain("Exception", model.Failure, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMachineThatCannotStartABackupDoesNotOfferTheButton()
    {
        var model = new RepositoriesViewModel(new FixedHealth(Health(HealthVerdict.Unproven)), new FakeProof(true));
        await model.RefreshAsync(CancellationToken.None);

        Assert.False(model.CanBackupNow);

        // And asking anyway does nothing rather than throwing at whoever wired the screen.
        await model.BackupNowAsync(Assert.Single(model.Repositories), CancellationToken.None);
        Assert.Null(model.Failure);
    }

    [Fact]
    public async Task AFailureCanBePutAwayOnceItHasBeenRead()
    {
        var model = new RepositoriesViewModel(
            new FixedHealth(Health(HealthVerdict.Unproven)),
            new FakeProof(true),
            backup: new FakeBackup(false, "the repository was unreachable"));

        await model.RefreshAsync(CancellationToken.None);
        await model.BackupNowAsync(Assert.Single(model.Repositories), CancellationToken.None);
        Assert.NotNull(model.Failure);

        model.ClearFailure();

        Assert.Null(model.Failure);
    }

    private static RepositoryHealth Health(HealthVerdict verdict, string? code = null)
    {
        var facts = new RepositoryFacts(
            "a",
            "documents",
            LastBackupAt: code == "backup-overdue" ? Now.AddDays(-30) : Now.AddHours(-1),
            LastHealthyCheckAt: verdict == HealthVerdict.Recoverable ? Now.AddDays(-1) : null,
            LastProvenRestoreAt: verdict == HealthVerdict.Recoverable ? Now.AddDays(-2) : null,
            KitPresent: true,
            StorageImmutable: true,
            StorageProtectionNow: StorageProtectionStatus.Immutable);

        return HealthAssessor.Assess(facts, Now);
    }

    private sealed class FixedHealth(params RepositoryHealth[] repositories) : IHealthSource
    {
        public Task<HealthReadResult> ReadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new HealthReadResult(HealthStoreState.Active, new HealthReport(Now, repositories)));
    }

    private sealed class SwitchingHealth(params RepositoryHealth[] reports) : IHealthSource
    {
        private int _call;

        public Task<HealthReadResult> ReadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new HealthReadResult(HealthStoreState.Active, new HealthReport(Now, [reports[Math.Min(_call++, reports.Length - 1)]])));
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

    private sealed class ThrowingBackup : IBackupNow
    {
        public Task<BackupNowResult> BackupAsync(string repositoryId, CancellationToken cancellationToken) =>
            throw new UnauthorizedAccessException("Access to the path is denied.");
    }
}
