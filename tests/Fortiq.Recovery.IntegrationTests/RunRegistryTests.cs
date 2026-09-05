using Fortiq.Application;
using Fortiq.Domain;
using Fortiq.Infrastructure.Runs;

namespace Fortiq.Recovery.IntegrationTests;

/// <summary>
/// The registry that makes "no other run is working on this repository" a fact rather than an
/// assumption. Reconciliation depends on it: it removes locks whose owner cannot be proven dead.
/// </summary>
public sealed class RunRegistryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "fortiq-runs-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SharedRunsOfTheSameRepositoryCoexist()
    {
        var registry = Registry();
        var repository = RepositoryId.Create();

        await using var backup = await registry.BeginAsync(repository, OperationKind.Backup, Guid.NewGuid(), RunExclusivity.Shared, CancellationToken.None);
        await using var listing = await registry.BeginAsync(repository, OperationKind.Snapshots, Guid.NewGuid(), RunExclusivity.Shared, CancellationToken.None);

        Assert.Equal(RunExclusivity.Shared, backup.Exclusivity);
        Assert.NotEqual(backup.OperationId, listing.OperationId);
    }

    [Fact]
    public async Task AnExclusiveRunWaitsForTheRepositoryAndThenGivesUp()
    {
        // A separate registry instance opens its own handles, which is what a second process does;
        // the arbitration is the operating system's, not this object's.
        var holder = Registry();
        var claimant = Registry(TimeSpan.FromMilliseconds(200));
        var repository = RepositoryId.Create();

        await using var backup = await holder.BeginAsync(repository, OperationKind.Backup, Guid.NewGuid(), RunExclusivity.Shared, CancellationToken.None);

        var failure = await Assert.ThrowsAsync<RepositoryBusyException>(
            () => claimant.BeginAsync(repository, OperationKind.Reconcile, Guid.NewGuid(), RunExclusivity.Exclusive, CancellationToken.None));

        Assert.Contains("needs the repository to itself", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASharedRunWaitsWhileAnExclusiveOneHoldsTheRepository()
    {
        var holder = Registry();
        var claimant = Registry(TimeSpan.FromMilliseconds(200));
        var repository = RepositoryId.Create();

        await using var reconcile = await holder.BeginAsync(repository, OperationKind.Reconcile, Guid.NewGuid(), RunExclusivity.Exclusive, CancellationToken.None);

        await Assert.ThrowsAsync<RepositoryBusyException>(
            () => claimant.BeginAsync(repository, OperationKind.Backup, Guid.NewGuid(), RunExclusivity.Shared, CancellationToken.None));
    }

    [Fact]
    public async Task RunsOfDifferentRepositoriesDoNotBlockEachOther()
    {
        var registry = Registry();

        await using var first = await registry.BeginAsync(RepositoryId.Create(), OperationKind.Reconcile, Guid.NewGuid(), RunExclusivity.Exclusive, CancellationToken.None);
        await using var second = await registry.BeginAsync(RepositoryId.Create(), OperationKind.Reconcile, Guid.NewGuid(), RunExclusivity.Exclusive, CancellationToken.None);

        Assert.Equal(RunExclusivity.Exclusive, first.Exclusivity);
        Assert.Equal(RunExclusivity.Exclusive, second.Exclusivity);
    }

    [Fact]
    public async Task TheRepositoryIsFreeAgainOnceARunEnds()
    {
        var registry = Registry(TimeSpan.FromMilliseconds(200));
        var repository = RepositoryId.Create();

        var backup = await registry.BeginAsync(repository, OperationKind.Backup, Guid.NewGuid(), RunExclusivity.Shared, CancellationToken.None);
        await backup.DisposeAsync();

        await using var reconcile = await registry.BeginAsync(repository, OperationKind.Reconcile, Guid.NewGuid(), RunExclusivity.Exclusive, CancellationToken.None);
        Assert.Equal(RunExclusivity.Exclusive, reconcile.Exclusivity);
    }

    [Fact]
    public async Task ReconciliationRefusesToRunWhileAnotherOperationHoldsTheRepository()
    {
        var registry = Registry(TimeSpan.FromMilliseconds(200));
        var repository = new RepositoryDescriptor(RepositoryId.Create(), Path.GetFullPath("repository"));
        var engine = new RecordingRepository();
        var registered = new RegisteredRunBackupRepository(engine, registry);

        // Something else is working on the repository; reconciliation clears locks it cannot prove
        // are dead, so it must not proceed.
        await using var other = await Registry().BeginAsync(repository.Id, OperationKind.Backup, Guid.NewGuid(), RunExclusivity.Shared, CancellationToken.None);

        await Assert.ThrowsAsync<RepositoryBusyException>(
            () => registered.ReconcileAsync(new ReconcileRepository(repository), CancellationToken.None));

        Assert.False(engine.Reconciled, "Reconciliation reached the engine while another run held the repository.");
    }

    [Fact]
    public async Task ReconciliationProceedsWhenNothingElseIsRunning()
    {
        var registry = Registry(TimeSpan.FromMilliseconds(200));
        var repository = new RepositoryDescriptor(RepositoryId.Create(), Path.GetFullPath("repository"));
        var engine = new RecordingRepository();
        var registered = new RegisteredRunBackupRepository(engine, registry);

        await registered.ReconcileAsync(new ReconcileRepository(repository), CancellationToken.None);

        Assert.True(engine.Reconciled);

        // The run is over, so the repository is available again.
        await using var next = await registry.BeginAsync(repository.Id, OperationKind.Backup, Guid.NewGuid(), RunExclusivity.Shared, CancellationToken.None);
        Assert.Equal(RunExclusivity.Shared, next.Exclusivity);
    }

    [Fact]
    public async Task ARunRecordsWhoIsWorkingOnTheRepository()
    {
        var registry = Registry();
        var repository = RepositoryId.Create();
        var operationId = Guid.NewGuid();

        var path = Path.Combine(_directory, $"{repository.ToString().ToLowerInvariant()}.run");
        await using (var run = await registry.BeginAsync(repository, OperationKind.Reconcile, operationId, RunExclusivity.Exclusive, CancellationToken.None))
        {
            Assert.Equal(operationId, run.OperationId);
        }

        // Diagnostics only: the lock lives in the handle, so this content never decides whether the
        // repository is busy.
        using var document = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(path));
        Assert.Equal("fortiq.repository-run", document.RootElement.GetProperty("schema").GetString());
        Assert.Equal(operationId, document.RootElement.GetProperty("operationId").GetGuid());
        Assert.Equal(Environment.ProcessId, document.RootElement.GetProperty("processId").GetInt32());
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private FileSystemRepositoryRunRegistry Registry(TimeSpan? wait = null) =>
        new(_directory, wait ?? TimeSpan.FromMilliseconds(500));

    [Theory]
    [InlineData(PruneMode.ForgetOnly)]
    [InlineData(PruneMode.ForgetAndPrune)]
    public async Task RetentionTakesTheRepositoryToItselfInEitherMode(PruneMode mode)
    {
        var holder = Registry();
        var repository = new RepositoryDescriptor(RepositoryId.Create(), @"C:
epo");

        var adapter = new RegisteredRunBackupRepository(
            new RecordingRepository(),
            Registry(TimeSpan.FromMilliseconds(200)));

        // Something else is already reading the repository.
        await using var reading = await holder.BeginAsync(
            repository.Id, OperationKind.Snapshots, Guid.NewGuid(), RunExclusivity.Shared, CancellationToken.None);

        // Forgetting is not merely bookkeeping. It decides what to keep from the snapshots as they
        // stand, so a backup landing partway through applies the policy to a repository that no
        // longer exists; and it removes snapshots a restore may be about to read, which would be
        // recorded as a drill that could not prove recovery.
        await Assert.ThrowsAsync<RepositoryBusyException>(
            () => adapter.ApplyRetentionAsync(
                new ApplyRetention(repository, new RetentionPolicy(), mode),
                CancellationToken.None));
    }

    [Fact]
    public async Task NothingElseStartsWhileRetentionIsRunning()
    {
        var holder = Registry();
        var repository = RepositoryId.Create();

        await using var retention = await holder.BeginAsync(
            repository, OperationKind.Retention, Guid.NewGuid(), RunExclusivity.Exclusive, CancellationToken.None);

        var claimant = Registry(TimeSpan.FromMilliseconds(200));

        // A backup that started here would be writing into a repository whose data is being deleted.
        await Assert.ThrowsAsync<RepositoryBusyException>(
            () => claimant.BeginAsync(repository, OperationKind.Backup, Guid.NewGuid(), RunExclusivity.Shared, CancellationToken.None));

        await Assert.ThrowsAsync<RepositoryBusyException>(
            () => claimant.BeginAsync(repository, OperationKind.Restore, Guid.NewGuid(), RunExclusivity.Shared, CancellationToken.None));
    }

    [Fact]
    public async Task ADrillHoldsTheRepositoryFromChoosingASnapshotUntilItHasRestoredIt()
    {
        var registry = Registry();
        var repository = RepositoryId.Create();

        // What a drill holds for the whole of its work.
        await using var drill = await registry.BeginAsync(
            repository, OperationKind.Restore, Guid.NewGuid(), RunExclusivity.Shared, CancellationToken.None);

        // Retention cannot slip in and forget the snapshot the drill has just chosen.
        await Assert.ThrowsAsync<RepositoryBusyException>(
            () => Registry(TimeSpan.FromMilliseconds(200)).BeginAsync(
                repository, OperationKind.Retention, Guid.NewGuid(), RunExclusivity.Exclusive, CancellationToken.None));

        // The drill's own listing and restore still register underneath it: shared runs nest, or the
        // drill would deadlock against itself.
        await using var listing = await registry.BeginAsync(
            repository, OperationKind.Snapshots, Guid.NewGuid(), RunExclusivity.Shared, CancellationToken.None);
        await using var restore = await registry.BeginAsync(
            repository, OperationKind.Restore, Guid.NewGuid(), RunExclusivity.Shared, CancellationToken.None);

        Assert.NotEqual(drill.OperationId, restore.OperationId);
    }

    private sealed class RecordingRepository : IBackupRepository
    {
        internal bool Reconciled { get; private set; }

        public Task<RepositoryDescriptor> InitializeAsync(InitializeRepository command, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<BackupReceipt> CreateSnapshotAsync(CreateSnapshot command, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<SnapshotDescriptor>> ListSnapshotsAsync(ListSnapshots query, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<CheckReceipt> CheckAsync(CheckRepository command, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<RestoreReceipt> RestoreAsync(RestoreSnapshot command, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task ReconcileAsync(ReconcileRepository command, CancellationToken cancellationToken)
        {
            Reconciled = true;
            return Task.CompletedTask;
        }

        public Task<RetentionReceipt> ApplyRetentionAsync(ApplyRetention command, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<SnapshotFileEntry>> ListFilesAsync(ListSnapshotFiles query, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
