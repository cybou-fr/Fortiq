using Fortiq.Application;

namespace Fortiq.Operations;

/// <summary>
/// How much a restore is expected to write, read out of the repository's own backup receipts.
/// </summary>
/// <remarks>
/// Separated from <see cref="ProvenRestore"/> because the interesting part is a choice between two
/// numbers that look interchangeable and are not, and a choice like that is worth being able to state
/// a test about without standing up a repository first.
/// </remarks>
public static class RestoreSpaceEstimate
{
    /// <summary>The receipt metric that records the logical size of what was backed up.</summary>
    /// <remarks>
    /// Not <c>bytesAdded</c>. Added bytes are what deduplication could not avoid writing, so a snapshot
    /// of an 800 GB tree whose contents barely changed adds a few gigabytes. Sizing a restore from that
    /// reserves a fiftieth of what the restore needs, and the drill fails partway through having
    /// already been told there was room.
    /// </remarks>
    public const string LogicalSizeMetric = "bytesProcessed";

    /// <summary>
    /// The expected size of restoring <paramref name="snapshotId"/>, or 0 when the receipts do not say.
    /// </summary>
    /// <param name="receipts">Every receipt held for the repository.</param>
    /// <param name="snapshotId">The snapshot about to be restored.</param>
    /// <remarks>
    /// The receipt for that exact snapshot is preferred. Any other snapshot's size is a guess about
    /// this one, and a repository whose source has grown since is precisely where the guess is wrong
    /// in the direction that matters. Where no receipt names it - it predates receipts, or another
    /// machine wrote it - the newest successful backup stands in, and the caller's headroom is what
    /// makes that safe rather than this number being right.
    /// </remarks>
    public static long ForSnapshot(IEnumerable<OperationReceipt> receipts, string snapshotId)
    {
        ArgumentNullException.ThrowIfNull(receipts);

        var backups = receipts
            .Where(receipt => receipt.Operation == OperationKind.Backup && receipt.EngineResult == EngineResult.Succeeded)
            .ToList();

        var exact = backups.FirstOrDefault(receipt =>
            string.Equals(receipt.SnapshotId, snapshotId, StringComparison.OrdinalIgnoreCase));

        if (LogicalSize(exact) is { } exactBytes)
        {
            return exactBytes;
        }

        // The newest backup that actually recorded a size, not simply the newest. A receipt written
        // before the metric existed, or by an engine run that reported nothing, says nothing about how
        // big the repository is - and letting it stand in front of an older receipt that does know
        // would throw away the only real number available.
        return backups
            .OrderByDescending(receipt => receipt.CompletedAt)
            .Select(LogicalSize)
            .FirstOrDefault(size => size is not null) ?? 0;
    }

    private static long? LogicalSize(OperationReceipt? receipt) =>
        receipt is not null &&
        receipt.Metrics.TryGetValue(LogicalSizeMetric, out var bytes) &&
        bytes > 0
            ? bytes
            : null;
}
