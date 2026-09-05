using Fortiq.Application;
using Fortiq.Infrastructure.Receipts;
using Fortiq.Infrastructure.Keys;
using Fortiq.Scheduling;
using System.Runtime.Versioning;
using Fortiq.Monitoring;
using Fortiq.Operations;

namespace Fortiq.Recovery.IntegrationTests;

public sealed class RestoreEvidenceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "fortiq-proof-evidence-" + Guid.NewGuid().ToString("N"));
    private static readonly EngineIdentity Engine = new("restic", "0.19.1", new string('a', 64));

    [Fact]
    public async Task EngineSuccessFollowedByFailedReconciliationNeverBecomesProof()
    {
        var store = new FileSystemOperationReceiptStore(_directory);
        var recorder = new RestoreProofRecorder(store);
        await Assert.ThrowsAsync<RestoreProofFailedException>(() => recorder.RecordAsync("repository", Engine, async () =>
        {
            await store.SaveAsync(new OperationReceipt(Guid.NewGuid(), OperationKind.Restore, "repository", Engine,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, EngineResult.Succeeded, "snapshot", null,
                new Dictionary<string, long>(), []), CancellationToken.None);
            Assert.Null(Assert.Single(await ReceiptHistory.ReadAsync(_directory, CancellationToken.None)).LastProvenRestoreAt);
            throw new RestoreProofFailedException("Restored byte count differs.");
        }));
        var evidence = Assert.Single(await ReceiptHistory.ReadAsync(_directory, CancellationToken.None));
        Assert.Null(evidence.LastProvenRestoreAt);
        Assert.Equal("Restored byte count differs.", evidence.LastFailure);
    }

    [Fact]
    public async Task ProofIsVisibleOnlyAfterVerificationCompletes()
    {
        var recorder = new RestoreProofRecorder(new FileSystemOperationReceiptStore(_directory));
        await recorder.RecordAsync("repository", Engine, async () =>
        {
            Assert.Empty(await ReceiptHistory.ReadAsync(_directory, CancellationToken.None));
            return new RestoreProof("repository", "snapshot", DateTimeOffset.UtcNow, 2, 1, 42);
        });
        Assert.NotNull(Assert.Single(await ReceiptHistory.ReadAsync(_directory, CancellationToken.None)).LastProvenRestoreAt);
    }

    [Fact]
    public async Task LostEvidenceIsNotReportedAsDurablyProven()
    {
        var recorder = new RestoreProofRecorder(new UnwritableStore());
        await Assert.ThrowsAsync<IOException>(() => recorder.RecordAsync("repository", Engine,
            () => Task.FromResult(new RestoreProof("repository", "snapshot", DateTimeOffset.UtcNow, 2, 1, 42))));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task MissingEnginePreflightIsRecordedAsAFailedProof()
    {
        var kitPath = Path.Combine(_directory, "kit");
        var receipts = Path.Combine(_directory, "receipts");
        using var lease = new BufferKeyLease(new byte[32]);
        await RecoveryKitStore.WriteAsync(kitPath, "repository",
            new RecoveryKitEngine(Engine.Name, Engine.Version, Engine.Sha256),
            [Bip39RecoveryEnvelope.Wrap(new byte[32], Bip39Mnemonic.Create(), lease)], null, CancellationToken.None);
        var proof = new ProvenRestore(Path.Combine(_directory, "missing-engine"), Path.Combine(_directory, "work"), receiptDirectory: receipts);
        var schedule = new BackupSchedule("documents", "repository", kitPath, "source", "source", new EveryInterval(TimeSpan.FromHours(1)));
        await Assert.ThrowsAnyAsync<IOException>(() => proof.ProveAsync(schedule, CancellationToken.None));
        var evidence = Assert.Single(await ReceiptHistory.ReadAsync(receipts, CancellationToken.None));
        Assert.Null(evidence.LastProvenRestoreAt);
        Assert.NotNull(evidence.LastFailure);
    }

    private sealed class UnwritableStore : IOperationReceiptStore
    {
        public Task<string> SaveAsync(OperationReceipt receipt, CancellationToken cancellationToken) => throw new IOException("Disk full.");
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
