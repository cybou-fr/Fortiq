using Fortiq.Application;

namespace Fortiq.Operations;

/// <summary>Persists the result of the complete restore verification, after disk reconciliation.</summary>
public sealed class RestoreProofRecorder(IOperationReceiptStore store, TimeProvider? clock = null)
{
    private readonly IOperationReceiptStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task<RestoreProof> RecordAsync(
        string repositoryId, EngineIdentity engine, Func<Task<RestoreProof>> verify)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryId);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(verify);
        var started = _clock.GetUtcNow();
        var id = Guid.NewGuid();
        RestoreProof proof;
        try
        {
            proof = await verify();
        }
        catch (Exception error)
        {
            try
            {
                await _store.SaveAsync(new OperationReceipt(
                    id, OperationKind.RestoreProof, repositoryId, engine, started, _clock.GetUtcNow(),
                    error is OperationCanceledException ? EngineResult.Cancelled : EngineResult.Failed,
                    null, null, new Dictionary<string, long>(), [error.Message]), CancellationToken.None);
            }
            catch (Exception writeError) when (writeError is IOException or UnauthorizedAccessException)
            {
                // Keep the verification failure; the scheduled drill also records its failed state.
            }

            throw;
        }

        // A proof whose evidence cannot be saved must not be reported as durably proven.
        await _store.SaveAsync(new OperationReceipt(
            id, OperationKind.RestoreProof, repositoryId, engine, started, _clock.GetUtcNow(),
            EngineResult.Succeeded, proof.SnapshotId, null,
            new Dictionary<string, long>
            {
                ["filesRestored"] = checked((long)proof.FilesOnDisk),
                ["bytesRestored"] = checked((long)proof.BytesRestored)
            }, []), CancellationToken.None);
        return proof;
    }
}
