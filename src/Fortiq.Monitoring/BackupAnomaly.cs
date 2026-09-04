namespace Fortiq.Monitoring;

/// <summary>One backup, in the terms an anomaly can be judged from.</summary>
public sealed record BackupObservation(
    DateTimeOffset CompletedAt,
    string? SnapshotId,
    long BytesProcessed,
    long BytesAdded,
    long FilesChanged);

/// <summary>What is unusual about a backup, compared with this repository's own history.</summary>
public enum AnomalyKind
{
    /// <summary>Deduplication saved almost nothing, as it cannot when files have been rewritten.</summary>
    DeduplicationCollapsed,

    /// <summary>Far more unique data was written than this repository usually writes.</summary>
    AddedDataSpike,

    /// <summary>Far more of the source changed than usually changes.</summary>
    ChangedFileSpike
}

/// <summary>
/// Something worth a person's attention about one backup. Deliberately not a verdict: this reports
/// what is unusual and by how much, and says nothing about the cause.
/// </summary>
public sealed record BackupAnomaly(AnomalyKind Kind, string Detail, double Ratio);

/// <summary>
/// Compares a backup against the recent history of the same repository and reports what stands out.
/// </summary>
/// <remarks>
/// This exists because ransomware that encrypts a source in place leaves the source almost exactly
/// the same size, so the number that would most obviously reveal it - how many bytes were backed up
/// - barely moves. What does move is how much of that was new: deduplication cannot help when every
/// file has been rewritten, so the added bytes jump towards the size of the whole source.
/// <para>
/// It is equally true that a person importing a video library produces the same shape. Nothing here
/// decides which happened, and nothing acts on the answer: an anomaly is surfaced with its numbers
/// for someone to look at. A backup tool that stopped backing up on a guess would be a tool that an
/// unusual Tuesday could disarm, and the encrypted files would still need backing up.
/// </para>
/// <para>
/// The baseline is the repository's own median, not a configured threshold. Repositories differ by
/// orders of magnitude in how much they churn, and a number chosen in advance is wrong for almost
/// all of them. The median rather than the mean, so one previous spike does not raise the bar
/// enough to hide the next one.
/// </para>
/// </remarks>
public static class BackupAnomalyDetector
{
    /// <summary>
    /// How much of a backup must be newly written data before deduplication is considered to have
    /// collapsed. Ordinary incremental backups sit far below this; a source that has been rewritten
    /// wholesale approaches 1.
    /// </summary>
    public const double DeduplicationFloor = 0.5;

    /// <summary>How many times the usual figure counts as a spike.</summary>
    public const double SpikeMultiple = 10;

    /// <summary>
    /// The fewest previous backups needed before a comparison means anything. Below this there is no
    /// history to be unusual against, and reporting on two data points would produce alarms for
    /// every repository in its first week.
    /// </summary>
    public const int MinimumHistory = 4;

    /// <summary>
    /// Judges the most recent backup against those before it. <paramref name="history"/> is in any
    /// order; the newest observation is the one judged, and the rest form the baseline.
    /// </summary>
    public static IReadOnlyList<BackupAnomaly> Inspect(IReadOnlyList<BackupObservation> history)
    {
        ArgumentNullException.ThrowIfNull(history);

        var ordered = history.OrderByDescending(backup => backup.CompletedAt).ToArray();
        if (ordered.Length <= MinimumHistory)
        {
            return [];
        }

        var latest = ordered[0];
        var baseline = ordered[1..];
        var anomalies = new List<BackupAnomaly>();

        // Deduplication collapse is judged against this backup alone, not against history: it is a
        // statement about one snapshot, and it is the signal that survives a source whose size did
        // not change.
        if (latest.BytesProcessed > 0)
        {
            var share = (double)latest.BytesAdded / latest.BytesProcessed;
            if (share >= DeduplicationFloor)
            {
                anomalies.Add(new BackupAnomaly(
                    AnomalyKind.DeduplicationCollapsed,
                    $"{Percentage(share)} of this backup was newly written data. Deduplication normally "
                    + "avoids most of it, and cannot when files have been rewritten in place.",
                    share));
            }
        }

        Spike(
            anomalies,
            AnomalyKind.AddedDataSpike,
            latest.BytesAdded,
            baseline.Select(backup => backup.BytesAdded),
            "newly written bytes");

        Spike(
            anomalies,
            AnomalyKind.ChangedFileSpike,
            latest.FilesChanged,
            baseline.Select(backup => backup.FilesChanged),
            "changed or new files");

        return anomalies;
    }

    private static void Spike(
        List<BackupAnomaly> anomalies,
        AnomalyKind kind,
        long latest,
        IEnumerable<long> baseline,
        string what)
    {
        var usual = Median(baseline);

        // A repository that usually writes nothing cannot have a meaningful multiple taken against
        // it: every first change would be infinitely more than nothing.
        if (usual <= 0 || latest < usual * SpikeMultiple)
        {
            return;
        }

        var ratio = (double)latest / usual;
        anomalies.Add(new BackupAnomaly(
            kind,
            $"This backup recorded {latest:N0} {what}, about {ratio:N0} times the usual {usual:N0}.",
            ratio));
    }

    private static long Median(IEnumerable<long> values)
    {
        var sorted = values.Order().ToArray();
        if (sorted.Length == 0)
        {
            return 0;
        }

        var middle = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }

    private static string Percentage(double share) =>
        (share * 100).ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + "%";
}
