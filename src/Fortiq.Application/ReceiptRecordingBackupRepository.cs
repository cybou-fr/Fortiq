using Fortiq.Domain;

namespace Fortiq.Application;

/// <summary>
/// Records one receipt per engine operation, including the operations that fail. A receipt is
/// evidence, not a source of truth: it never carries a secret, and losing every receipt does not
/// affect whether the repository can be restored.
/// </summary>
public sealed class ReceiptRecordingBackupRepository : IBackupRepository
{
    private readonly IBackupRepository _inner;
    private readonly EngineIdentity _engine;
    private readonly IOperationReceiptStore _store;
    private readonly TimeProvider _clock;

    public ReceiptRecordingBackupRepository(
        IBackupRepository inner,
        EngineIdentity engine,
        IOperationReceiptStore store,
        TimeProvider? clock = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _clock = clock ?? TimeProvider.System;
    }

    public Task<RepositoryDescriptor> InitializeAsync(InitializeRepository command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return RecordAsync(
            OperationKind.Initialize,
            () => _inner.InitializeAsync(command, cancellationToken),
            repository => repository.Id.ToString(),
            _ => null,
            _ => NoMetrics,
            null,
            repositoryId: null,
            cancellationToken);
    }

    public Task<BackupReceipt> CreateSnapshotAsync(CreateSnapshot command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return RecordAsync(
            OperationKind.Backup,
            () => _inner.CreateSnapshotAsync(command, cancellationToken),
            _ => command.Repository.Id.ToString(),
            receipt => receipt.SnapshotId,
            receipt => new Dictionary<string, long>(StringComparer.Ordinal)
            {
                ["filesProcessed"] = ToMetric(receipt.FilesProcessed),
                ["bytesProcessed"] = ToMetric(receipt.BytesProcessed)
            },
            new ReceiptSource("directory", command.SourceStableId),
            command.Repository.Id.ToString(),
            cancellationToken);
    }

    public Task<IReadOnlyList<SnapshotDescriptor>> ListSnapshotsAsync(ListSnapshots query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        return RecordAsync(
            OperationKind.Snapshots,
            () => _inner.ListSnapshotsAsync(query, cancellationToken),
            _ => query.Repository.Id.ToString(),
            _ => null,
            snapshots => new Dictionary<string, long>(StringComparer.Ordinal) { ["snapshots"] = snapshots.Count },
            null,
            query.Repository.Id.ToString(),
            cancellationToken);
    }

    public Task<CheckReceipt> CheckAsync(CheckRepository command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return RecordAsync(
            OperationKind.Check,
            () => _inner.CheckAsync(command, cancellationToken),
            _ => command.Repository.Id.ToString(),
            _ => null,
            _ => NoMetrics,
            null,
            command.Repository.Id.ToString(),
            cancellationToken);
    }

    public Task<RestoreReceipt> RestoreAsync(RestoreSnapshot command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return RecordAsync(
            OperationKind.Restore,
            () => _inner.RestoreAsync(command, cancellationToken),
            _ => command.Repository.Id.ToString(),
            receipt => receipt.SnapshotId,
            receipt => new Dictionary<string, long>(StringComparer.Ordinal)
            {
                ["filesRestored"] = ToMetric(receipt.FilesRestored),
                ["bytesRestored"] = ToMetric(receipt.BytesRestored)
            },
            null,
            command.Repository.Id.ToString(),
            cancellationToken);
    }

    public async Task ReconcileAsync(ReconcileRepository command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await RecordAsync<object?>(
            OperationKind.Reconcile,
            async () =>
            {
                await _inner.ReconcileAsync(command, cancellationToken);
                return null;
            },
            _ => command.Repository.Id.ToString(),
            _ => null,
            _ => NoMetrics,
            null,
            command.Repository.Id.ToString(),
            cancellationToken);
    }

    private async Task<TResult> RecordAsync<TResult>(
        OperationKind operation,
        Func<Task<TResult>> execute,
        Func<TResult, string> resolveRepositoryId,
        Func<TResult, string?> resolveSnapshotId,
        Func<TResult, IReadOnlyDictionary<string, long>> resolveMetrics,
        ReceiptSource? source,
        string? repositoryId,
        CancellationToken cancellationToken)
    {
        var operationId = Guid.NewGuid();
        var startedAt = _clock.GetUtcNow();
        try
        {
            var result = await execute();
            await SaveAsync(
                new OperationReceipt(
                    operationId,
                    operation,
                    resolveRepositoryId(result),
                    _engine,
                    startedAt,
                    _clock.GetUtcNow(),
                    OperationResult.Succeeded,
                    resolveSnapshotId(result),
                    source,
                    resolveMetrics(result),
                    []),
                cancellationToken);
            return result;
        }
        catch (Exception error)
        {
            // A failed or cancelled operation still produces evidence, and it is never marked
            // succeeded. Only the engine's own diagnostics are recorded; Fortiq passes no secret to
            // the engine, so its messages carry paths and status text at most.
            await SaveAsync(
                new OperationReceipt(
                    operationId,
                    operation,
                    repositoryId ?? string.Empty,
                    _engine,
                    startedAt,
                    _clock.GetUtcNow(),
                    error is OperationCanceledException ? OperationResult.Cancelled : OperationResult.Failed,
                    SnapshotId: null,
                    source,
                    NoMetrics,
                    [error.Message]),
                CancellationToken.None);
            throw;
        }
    }

    private async Task SaveAsync(OperationReceipt receipt, CancellationToken cancellationToken)
    {
        try
        {
            await _store.SaveAsync(receipt, cancellationToken);
        }
        catch (IOException)
        {
            // Evidence that cannot be written must not turn a completed operation into a failure,
            // and must not hide the original error of a failed one.
        }
    }

    private static readonly IReadOnlyDictionary<string, long> NoMetrics =
        new Dictionary<string, long>(StringComparer.Ordinal);

    private static long ToMetric(ulong value) => value > long.MaxValue ? long.MaxValue : (long)value;
}
