using Fortiq.Application;
using Fortiq.Infrastructure.Keys;
using Fortiq.Infrastructure.Receipts;
using Fortiq.Monitoring;
using Fortiq.Operations;
using Fortiq.Scheduling;

namespace Fortiq.Recovery.IntegrationTests;

public sealed class DrillHealthEvidenceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "fortiq-drill-health-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ADrillThatFailsBeforeRestoreRemainsAtRiskAfterASuccessfulBackup()
    {
        var now = DateTimeOffset.UtcNow;
        var kitPath = Path.Combine(_directory, "kit");
        var receipts = Path.Combine(_directory, "receipts");
        var engine = new EngineIdentity("restic", "0.19.1", new string('a', 64));
        using var lease = new BufferKeyLease(new byte[32]);
        var kit = await RecoveryKitStore.WriteAsync(kitPath, "repository",
            new RecoveryKitEngine(engine.Name, engine.Version, engine.Sha256),
            [Bip39RecoveryEnvelope.Wrap(new byte[32], Bip39Mnemonic.Create(), lease)], null, CancellationToken.None);
        var schedule = new BackupSchedule("documents", "repository", kitPath, "source", "source", new EveryInterval(TimeSpan.FromHours(1)));
        var schedules = new MemorySchedules(schedule, new ScheduleState(schedule.DrillStateId,
            LastAttemptAt: now, LastFailure: "No space for the restore drill."));
        var receiptStore = new FileSystemOperationReceiptStore(receipts);
        foreach (var kind in new[] { OperationKind.Backup, OperationKind.Check, OperationKind.RestoreProof })
        {
            var at = kind == OperationKind.Backup ? now.AddMinutes(1) : now.AddMinutes(-1);
            await receiptStore.SaveAsync(new OperationReceipt(Guid.NewGuid(), kind, kit.RepositoryId, engine,
                at, at, EngineResult.Succeeded, "snapshot", null, new Dictionary<string, long>(), []), CancellationToken.None);
        }
        var publisher = new HealthPublisher(schedules, receipts, Path.Combine(_directory, "health.json"),
            Path.Combine(_directory, "fortiq.prom"), protection: new ImmutableStorage());
        var failed = Assert.Single((await publisher.PublishAsync(CancellationToken.None)).Repositories);
        Assert.Equal(HealthVerdict.AtRisk, failed.Verdict);
        Assert.Contains("No space", failed.Facts.LastFailure!, StringComparison.Ordinal);

        // An actual later proof, including one requested from the desktop, resolves the drill failure.
        var later = now.AddMinutes(2);
        await receiptStore.SaveAsync(new OperationReceipt(Guid.NewGuid(), OperationKind.RestoreProof, kit.RepositoryId,
            engine, later, later, EngineResult.Succeeded, "snapshot", null, new Dictionary<string, long>(), []), CancellationToken.None);
        Assert.Equal(HealthVerdict.Recoverable, Assert.Single((await publisher.PublishAsync(CancellationToken.None)).Repositories).Verdict);
    }

    private sealed class MemorySchedules(BackupSchedule schedule, ScheduleState drill) : IScheduleStore
    {
        public Task<IReadOnlyList<BackupSchedule>> ReadSchedulesAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<BackupSchedule>>([schedule]);
        public Task<ScheduleState> ReadStateAsync(string scheduleId, CancellationToken cancellationToken) => Task.FromResult(scheduleId == drill.ScheduleId ? drill : new ScheduleState(scheduleId));
        public Task WriteStateAsync(ScheduleState state, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class ImmutableStorage : IStorageProtectionInspector
    {
        public Task<StorageProtection> InspectAsync(string repositoryLocation, CancellationToken cancellationToken) =>
            Task.FromResult(new StorageProtection(true, RetentionMode.Compliance, TimeSpan.FromDays(30)));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
