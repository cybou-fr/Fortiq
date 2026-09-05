using Fortiq.Application;
using Fortiq.Operations;

namespace Fortiq.Monitoring.Tests;

/// <summary>
/// How much space a restore drill reserves, and the two numbers that must not be confused.
/// </summary>
/// <remarks>
/// The estimator looked up <c>backup.total_bytes_processed</c> and <c>backup.data_added</c> - the
/// engine's field names, not the receipt's. Nothing ever wrote those keys, so every lookup missed and
/// every drill fell through to a fixed 20 GB floor. The space check had never once consulted a real
/// size, and nothing failed to say so.
/// </remarks>
public sealed class RestoreSpaceEstimateTests
{
    private static readonly EngineIdentity Engine = new("restic", "0.19.1", new string('a', 64));

    [Fact]
    public void TheLogicalSizeOfTheSnapshotBeingRestoredIsUsed()
    {
        var receipts = new[]
        {
            Backup("snapshot-a", processed: 800L * 1024 * 1024 * 1024, added: 6L * 1024 * 1024 * 1024, minutesAgo: 60),
            Backup("snapshot-b", processed: 12L * 1024 * 1024 * 1024, added: 1L * 1024 * 1024 * 1024, minutesAgo: 10)
        };

        Assert.Equal(800L * 1024 * 1024 * 1024, RestoreSpaceEstimate.ForSnapshot(receipts, "snapshot-a"));
    }

    [Fact]
    public void DeduplicatedAddedBytesAreNeverTheAnswer()
    {
        // The distinction the whole file exists for. An 800 GB tree whose contents barely changed adds
        // a few gigabytes; sizing the restore from that reserves a fiftieth of what it needs, and the
        // drill runs out of disk having already been told there was room.
        var receipts = new[]
        {
            Backup("snapshot-a", processed: 800L * 1024 * 1024 * 1024, added: 6L * 1024 * 1024 * 1024, minutesAgo: 10)
        };

        var estimate = RestoreSpaceEstimate.ForSnapshot(receipts, "snapshot-a");

        Assert.NotEqual(6L * 1024 * 1024 * 1024, estimate);
        Assert.Equal(800L * 1024 * 1024 * 1024, estimate);
    }

    [Fact]
    public void ASnapshotWithNoReceiptFallsBackToTheNewestBackup()
    {
        var receipts = new[]
        {
            Backup("snapshot-old", processed: 100, added: 10, minutesAgo: 600),
            Backup("snapshot-new", processed: 500, added: 20, minutesAgo: 5)
        };

        // The snapshot predates receipts, or another machine wrote it. The newest backup is a guess,
        // which is why the caller adds headroom rather than treating this as a measurement.
        Assert.Equal(500, RestoreSpaceEstimate.ForSnapshot(receipts, "snapshot-unknown"));
    }

    [Fact]
    public void AFailedBackupIsNotEvidenceOfASize()
    {
        var receipts = new[]
        {
            Backup("snapshot-a", processed: 999, added: 1, minutesAgo: 5) with { EngineResult = EngineResult.Failed }
        };

        Assert.Equal(0, RestoreSpaceEstimate.ForSnapshot(receipts, "snapshot-a"));
    }

    [Fact]
    public void ARestoreReceiptIsNotEvidenceOfABackupSize()
    {
        var receipts = new[]
        {
            Backup("snapshot-a", processed: 999, added: 1, minutesAgo: 5) with { Operation = OperationKind.Restore }
        };

        Assert.Equal(0, RestoreSpaceEstimate.ForSnapshot(receipts, "snapshot-a"));
    }

    [Fact]
    public void NoReceiptsAtAllReportsNothingRatherThanGuessing()
    {
        // Zero is the caller's signal to use its floor. Returning some plausible number here would
        // make an unknown size indistinguishable from a measured one.
        Assert.Equal(0, RestoreSpaceEstimate.ForSnapshot([], "snapshot-a"));
    }

    [Fact]
    public void AZeroSizedMetricIsTreatedAsAbsent()
    {
        var receipts = new[]
        {
            Backup("snapshot-a", processed: 0, added: 0, minutesAgo: 5),
            Backup("snapshot-b", processed: 700, added: 7, minutesAgo: 50)
        };

        Assert.Equal(700, RestoreSpaceEstimate.ForSnapshot(receipts, "snapshot-a"));
    }

    private static OperationReceipt Backup(string snapshotId, long processed, long added, int minutesAgo)
    {
        var completed = DateTimeOffset.UtcNow.AddMinutes(-minutesAgo);

        return new OperationReceipt(
            Guid.NewGuid(),
            OperationKind.Backup,
            "repository-1",
            Engine,
            completed.AddMinutes(-1),
            completed,
            EngineResult.Succeeded,
            snapshotId,
            null,
            new Dictionary<string, long>(StringComparer.Ordinal)
            {
                ["bytesProcessed"] = processed,
                ["bytesAdded"] = added
            },
            []);
    }
}
