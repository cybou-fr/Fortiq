using Fortiq.Desktop.ViewModels;
using Fortiq.Monitoring;

namespace Fortiq.Desktop.Tests;

/// <summary>
/// The main screen. What it must never do is make a repository that has never been restored look
/// finished.
/// </summary>
public sealed class RepositoriesViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ABackedUpRepositoryThatWasNeverRestoredIsNotPresentedAsFinished()
    {
        var model = await ScreenAsync(Health(HealthVerdict.Unproven, "restore-never-proven"));

        var row = Assert.Single(model.Repositories);
        Assert.Equal("Backed up, but recovery has not been proven.", row.Summary);
        Assert.Contains("Nothing has ever been restored", row.Detail, StringComparison.Ordinal);
        Assert.Equal("Everything is backed up; recovery has not been proven for all of it.", model.Headline);
    }

    [Fact]
    public async Task ARepositoryAtRiskLeadsTheHeadline()
    {
        var model = await ScreenAsync(
            Health(HealthVerdict.Unproven, "restore-never-proven"),
            Health(HealthVerdict.AtRisk, "kit-missing", id: "b"));

        Assert.Equal("Something may not be recoverable today.", model.Headline);
    }

    [Fact]
    public async Task AScreenWithNoReportSaysSoRatherThanShowingNothing()
    {
        var model = new RepositoriesViewModel(new MissingHealth(), new FakeProof(true));

        await model.RefreshAsync(CancellationToken.None);

        // An empty list would read as "nothing is wrong", which is the opposite of what it means.
        Assert.Null(model.Failure);
        Assert.Equal(HealthStoreState.NotInitialized, model.State);
        Assert.Equal("Protect what matters before you need it.", model.Headline);
    }

    [Fact]
    public async Task ProvingRecoveryRefreshesWhatTheScreenClaims()
    {
        var health = new SwitchingHealth(
            Health(HealthVerdict.Unproven, "restore-never-proven"),
            Health(HealthVerdict.Recoverable));

        var model = new RepositoriesViewModel(health, new FakeProof(true));
        await model.RefreshAsync(CancellationToken.None);

        await model.ProveRecoveryAsync(Assert.Single(model.Repositories), CancellationToken.None);

        Assert.Null(model.Failure);
        Assert.Equal("Recoverable: checked and restored recently.", Assert.Single(model.Repositories).Summary);
    }

    [Fact]
    public async Task ARestoreThatDidNotProduceWhatItShouldIsReportedAsAFailure()
    {
        var model = new RepositoriesViewModel(
            new FixedHealth(Health(HealthVerdict.Unproven, "restore-never-proven")),
            new FakeProof(false));

        await model.RefreshAsync(CancellationToken.None);
        await model.ProveRecoveryAsync(Assert.Single(model.Repositories), CancellationToken.None);

        Assert.Equal("The restore did not produce what the snapshot says it should.", model.Failure);
    }

    [Fact]
    public async Task ARepositoryWithNoBackupHasNothingToProve()
    {
        var facts = new RepositoryFacts("a", "documents", null, null, null, KitPresent: true, StorageImmutable: true);
        var model = await ScreenAsync(HealthAssessor.Assess(facts, Now));

        Assert.False(Assert.Single(model.Repositories).CanProveRecovery);
    }

    private static async Task<RepositoriesViewModel> ScreenAsync(params RepositoryHealth[] repositories)
    {
        var model = new RepositoriesViewModel(new FixedHealth(repositories), new FakeProof(true));
        await model.RefreshAsync(CancellationToken.None);
        return model;
    }

    private static RepositoryHealth Health(HealthVerdict verdict, string? code = null, string id = "a")
    {
        var facts = new RepositoryFacts(
            id,
            "documents",
            LastBackupAt: Now.AddHours(-1),
            LastHealthyCheckAt: verdict == HealthVerdict.Recoverable ? Now.AddDays(-1) : null,
            LastProvenRestoreAt: verdict == HealthVerdict.Recoverable ? Now.AddDays(-2) : null,
            KitPresent: code != "kit-missing",
            StorageImmutable: true);

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

    private sealed class MissingHealth : IHealthSource
    {
        public Task<HealthReadResult> ReadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new HealthReadResult(HealthStoreState.NotInitialized));
    }

    private sealed class FakeProof(bool succeeds) : IProveRecovery
    {
        public Task<bool> ProveAsync(string repositoryId, CancellationToken cancellationToken) => Task.FromResult(succeeds);
    }
}
