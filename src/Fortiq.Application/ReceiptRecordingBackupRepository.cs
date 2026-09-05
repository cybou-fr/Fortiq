using Fortiq.Domain;

namespace Fortiq.Application;

/// <summary>
/// Assigns each operation its identity and records one receipt per operation, including the ones
/// that fail. A receipt is evidence, not a source of truth: it never carries a secret, and losing
/// every receipt does not affect whether the repository can be restored.
/// </summary>
/// <remarks>
/// Evidence is written once the engine has finished, using a cancellation token of its own. A caller
/// that cancels while the engine is already done still gets its receipt, and that receipt reports
/// what the engine did rather than what the caller wanted.
/// </remarks>
public sealed class ReceiptRecordingBackupRepository : IBackupRepository
{
    private static readonly IReadOnlyDictionary<string, long> NoMetrics =
        new Dictionary<string, long>(StringComparer.Ordinal);

    private readonly IBackupRepository _inner;
    private readonly EngineIdentity _engine;
    private readonly IOperationReceiptStore _store;
    private readonly IOperationEvidenceObserver? _observer;
    private readonly TimeProvider _clock;

    public ReceiptRecordingBackupRepository(
        IBackupRepository inner,
        EngineIdentity engine,
        IOperationReceiptStore store,
        IOperationEvidenceObserver? observer = null,
        TimeProvider? clock = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _observer = observer;
        _clock = clock ?? TimeProvider.System;
    }

    public Task<RepositoryDescriptor> InitializeAsync(InitializeRepository command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var identified = Identify(command, id => command with { OperationId = id });
        return RecordAsync(
            identified.OperationId,
            OperationKind.Initialize,
            () => _inner.InitializeAsync(identified, cancellationToken),
            repository => repository.Id.ToString(),
            _ => null,
            _ => NoMetrics,
            source: null,
            repositoryId: null,
            cancellationToken);
    }

    public Task<BackupReceipt> CreateSnapshotAsync(CreateSnapshot command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var identified = Identify(command, id => command with { OperationId = id });
        return RecordAsync(
            identified.OperationId,
            OperationKind.Backup,
            () => _inner.CreateSnapshotAsync(identified, cancellationToken),
            _ => identified.Repository.Id.ToString(),
            receipt => receipt.SnapshotId,
            receipt => new Dictionary<string, long>(StringComparer.Ordinal)
            {
                ["filesProcessed"] = ToMetric(receipt.FilesProcessed),
                ["bytesProcessed"] = ToMetric(receipt.BytesProcessed),
                // What deduplication could not avoid writing, and how much of the source was new or
                // rewritten. Recorded so that a later pass can compare a backup against this
                // repository's own history rather than against a number somebody guessed.
                ["bytesAdded"] = ToMetric(receipt.BytesAdded),
                ["filesChanged"] = ToMetric(receipt.FilesChanged)
            },
            new ReceiptSource(
                "directory",
                identified.SourceStableId,
                identified.Consistency == SourceConsistency.FileSystemSnapshot ? "snapshot" : "live"),
            identified.Repository.Id.ToString(),
            cancellationToken);
    }

    public Task<IReadOnlyList<SnapshotDescriptor>> ListSnapshotsAsync(ListSnapshots query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var identified = Identify(query, id => query with { OperationId = id });
        return RecordAsync(
            identified.OperationId,
            OperationKind.Snapshots,
            () => _inner.ListSnapshotsAsync(identified, cancellationToken),
            _ => identified.Repository.Id.ToString(),
            _ => null,
            snapshots => new Dictionary<string, long>(StringComparer.Ordinal) { ["snapshots"] = snapshots.Count },
            source: null,
            identified.Repository.Id.ToString(),
            cancellationToken);
    }

    public Task<IReadOnlyList<SnapshotFileEntry>> ListFilesAsync(ListSnapshotFiles query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var identified = Identify(query, id => query with { OperationId = id });
        return RecordAsync(
            identified.OperationId,
            OperationKind.Files,
            () => _inner.ListFilesAsync(identified, cancellationToken),
            _ => identified.Repository.Id.ToString(),
            _ => null,
            files => new Dictionary<string, long>(StringComparer.Ordinal) { ["files"] = files.Count },
            source: null,
            identified.Repository.Id.ToString(),
            cancellationToken);
    }

    public Task<CheckReceipt> CheckAsync(CheckRepository command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var identified = Identify(command, id => command with { OperationId = id });
        return RecordAsync(
            identified.OperationId,
            OperationKind.Check,
            () => _inner.CheckAsync(identified, cancellationToken),
            _ => identified.Repository.Id.ToString(),
            _ => null,
            _ => NoMetrics,
            source: null,
            identified.Repository.Id.ToString(),
            cancellationToken);
    }

    public Task<RestoreReceipt> RestoreAsync(RestoreSnapshot command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var identified = Identify(command, id => command with { OperationId = id });
        return RecordAsync(
            identified.OperationId,
            OperationKind.Restore,
            () => _inner.RestoreAsync(identified, cancellationToken),
            _ => identified.Repository.Id.ToString(),
            receipt => receipt.SnapshotId,
            receipt => new Dictionary<string, long>(StringComparer.Ordinal)
            {
                ["filesRestored"] = ToMetric(receipt.FilesRestored),
                ["bytesRestored"] = ToMetric(receipt.BytesRestored)
            },
            source: null,
            identified.Repository.Id.ToString(),
            cancellationToken);
    }

    public Task<RetentionReceipt> ApplyRetentionAsync(ApplyRetention command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var identified = Identify(command, id => command with { OperationId = id });
        return RecordAsync(
            identified.OperationId,
            OperationKind.Retention,
            () => _inner.ApplyRetentionAsync(identified, cancellationToken),
            _ => identified.Repository.Id.ToString(),
            _ => null,
            receipt => new Dictionary<string, long>(StringComparer.Ordinal)
            {
                ["snapshotsKept"] = receipt.KeptSnapshotIds.Count,
                ["snapshotsRemoved"] = receipt.RemovedSnapshotIds.Count,
                // Removing a snapshot is not the same as removing its data, and evidence that did
                // not distinguish the two would overstate what happened.
                ["dataPruned"] = receipt.Pruned ? 1 : 0
            },
            source: null,
            identified.Repository.Id.ToString(),
            cancellationToken);
    }

    public async Task ReconcileAsync(ReconcileRepository command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var identified = Identify(command, id => command with { OperationId = id });
        await RecordAsync<object?>(
            identified.OperationId,
            OperationKind.Reconcile,
            async () =>
            {
                await _inner.ReconcileAsync(identified, cancellationToken);
                return null;
            },
            _ => identified.Repository.Id.ToString(),
            _ => null,
            _ => NoMetrics,
            source: null,
            identified.Repository.Id.ToString(),
            cancellationToken);
    }

    private static TCommand Identify<TCommand>(TCommand command, Func<Guid, TCommand> withId)
        where TCommand : IOperationCommand =>
        command.OperationId == Guid.Empty ? withId(Guid.NewGuid()) : command;

    private async Task<TResult> RecordAsync<TResult>(
        Guid operationId,
        OperationKind operation,
        Func<Task<TResult>> execute,
        Func<TResult, string> resolveRepositoryId,
        Func<TResult, string?> resolveSnapshotId,
        Func<TResult, IReadOnlyDictionary<string, long>> resolveMetrics,
        ReceiptSource? source,
        string? repositoryId,
        CancellationToken cancellationToken)
    {
        var startedAt = _clock.GetUtcNow();
        TResult result;
        try
        {
            result = await execute();
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
                    error is OperationCanceledException ? EngineResult.Cancelled : EngineResult.Failed,
                    SnapshotId: null,
                    source,
                    NoMetrics,
                    [error.Message]));
            throw;
        }

        // The engine finished. Whether the caller has cancelled since then changes nothing about
        // what happened, so the receipt says succeeded and is written with its own token.
        _ = cancellationToken;
        await SaveAsync(
            new OperationReceipt(
                operationId,
                operation,
                resolveRepositoryId(result),
                _engine,
                startedAt,
                _clock.GetUtcNow(),
                EngineResult.Succeeded,
                resolveSnapshotId(result),
                source,
                resolveMetrics(result),
                []));

        return result;
    }

    private async Task SaveAsync(OperationReceipt receipt)
    {
        try
        {
            var location = await _store.SaveAsync(receipt, CancellationToken.None);
            _observer?.OnEvidence(new OperationEvidence(receipt, EvidenceWriteResult.Succeeded, location, null));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Evidence that cannot be written must not turn a completed operation into a failure,
            // and must not hide the original error of a failed one. It is reported separately, so a
            // lost receipt is visible rather than silent.
            _observer?.OnEvidence(new OperationEvidence(receipt, EvidenceWriteResult.Failed, null, error));
        }
    }

    private static long ToMetric(ulong value) => value > long.MaxValue ? long.MaxValue : (long)value;
}
