using Fortiq.Domain;

namespace Fortiq.Application;

/// <summary>
/// Registers every operation as a run against its repository, so "nothing else is working on this
/// repository" stops being an assumption.
/// </summary>
/// <remarks>
/// Reconciliation is the reason this exists. It clears locks whose owner cannot be proven dead - a
/// killed run can leave one whose process ID has already been reused - and that is only safe while
/// no other run is in flight. It therefore takes the repository exclusively; every other operation
/// takes it as shared, which is enough to make the exclusive claim mean something.
/// </remarks>
public sealed class RegisteredRunBackupRepository : IBackupRepository
{
    private readonly IBackupRepository _inner;
    private readonly IRepositoryRunRegistry _registry;

    public RegisteredRunBackupRepository(IBackupRepository inner, IRepositoryRunRegistry registry)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    public Task<RepositoryDescriptor> InitializeAsync(InitializeRepository command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        // A repository that does not exist yet has no identity to register a run against, and no
        // other run can be working on it.
        return _inner.InitializeAsync(command, cancellationToken);
    }

    public Task<BackupReceipt> CreateSnapshotAsync(CreateSnapshot command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return RunAsync(
            command.Repository.Id,
            OperationKind.Backup,
            command.OperationId,
            RunExclusivity.Shared,
            () => _inner.CreateSnapshotAsync(command, cancellationToken),
            cancellationToken);
    }

    public Task<IReadOnlyList<SnapshotDescriptor>> ListSnapshotsAsync(ListSnapshots query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        return RunAsync(
            query.Repository.Id,
            OperationKind.Snapshots,
            query.OperationId,
            RunExclusivity.Shared,
            () => _inner.ListSnapshotsAsync(query, cancellationToken),
            cancellationToken);
    }

    public Task<IReadOnlyList<SnapshotFileEntry>> ListFilesAsync(ListSnapshotFiles query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        return RunAsync(
            query.Repository.Id,
            OperationKind.Files,
            query.OperationId,
            RunExclusivity.Shared,
            () => _inner.ListFilesAsync(query, cancellationToken),
            cancellationToken);
    }

    public Task<CheckReceipt> CheckAsync(CheckRepository command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return RunAsync(
            command.Repository.Id,
            OperationKind.Check,
            command.OperationId,
            RunExclusivity.Shared,
            () => _inner.CheckAsync(command, cancellationToken),
            cancellationToken);
    }

    public Task<RestoreReceipt> RestoreAsync(RestoreSnapshot command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return RunAsync(
            command.Repository.Id,
            OperationKind.Restore,
            command.OperationId,
            RunExclusivity.Shared,
            () => _inner.RestoreAsync(command, cancellationToken),
            cancellationToken);
    }

    public Task<RetentionReceipt> ApplyRetentionAsync(ApplyRetention command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return RunAsync(
            command.Repository.Id,
            OperationKind.Retention,
            command.OperationId,
            // Both modes take the repository to themselves, not only the one that deletes data.
            // Forgetting decides what to keep from the list of snapshots as it stands, and a backup
            // landing partway through that decision means the policy was applied to a repository
            // that no longer exists. It also removes snapshots a restore may be about to read, and
            // a drill that failed because its snapshot was forgotten underneath it would be
            // recorded as recovery not proven - a false alarm about the one thing that matters.
            RunExclusivity.Exclusive,
            () => _inner.ApplyRetentionAsync(command, cancellationToken),
            cancellationToken);
    }

    public async Task ReconcileAsync(ReconcileRepository command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await RunAsync<object?>(
            command.Repository.Id,
            OperationKind.Reconcile,
            command.OperationId,
            RunExclusivity.Exclusive,
            async () =>
            {
                await _inner.ReconcileAsync(command, cancellationToken);
                return null;
            },
            cancellationToken);
    }

    private async Task<TResult> RunAsync<TResult>(
        RepositoryId repository,
        OperationKind operation,
        Guid operationId,
        RunExclusivity exclusivity,
        Func<Task<TResult>> execute,
        CancellationToken cancellationToken)
    {
        await using var run = await _registry.BeginAsync(
            repository,
            operation,
            operationId == Guid.Empty ? Guid.NewGuid() : operationId,
            exclusivity,
            cancellationToken);

        return await execute();
    }
}
