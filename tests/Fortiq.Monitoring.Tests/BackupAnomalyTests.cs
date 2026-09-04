using Fortiq.Monitoring;

namespace Fortiq.Monitoring.Tests;

/// <summary>
/// Noticing that a backup is unusual. The hard part is not spotting the spike; it is refusing to
/// call it ransomware, and refusing to let it change what Fortiq says about recoverability.
/// </summary>
public sealed class BackupAnomalyTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A repository that backs up 10 GB nightly and writes about 50 MB of new data.</summary>
    private static List<BackupObservation> Ordinary(int count) =>
        [.. Enumerable.Range(1, count).Select(day => new BackupObservation(
            Now.AddDays(-day),
            $"snapshot{day}",
            BytesProcessed: 10_000_000_000,
            BytesAdded: 50_000_000,
            FilesChanged: 40))];

    [Fact]
    public void ARepositoryWithNoHistoryIsNeverCalledUnusual()
    {
        // Two backups make every second backup unusual against the first. A repository in its first
        // week would alarm constantly, and an alarm that always fires is not read.
        var history = Ordinary(2);
        history.Insert(0, new BackupObservation(Now, "latest", 10_000_000_000, 9_000_000_000, 120_000));

        Assert.Empty(BackupAnomalyDetector.Inspect(history));
    }

    [Fact]
    public void ASourceRewrittenInPlaceIsNoticedEvenThoughItsSizeDidNotChange()
    {
        var history = Ordinary(6);

        // The shape encryption leaves: same bytes processed, but deduplication saved nothing,
        // because every file is different from the one stored before it.
        history.Insert(0, new BackupObservation(Now, "latest", 10_000_000_000, 9_800_000_000, 120_000));

        var anomalies = BackupAnomalyDetector.Inspect(history);

        Assert.Contains(anomalies, anomaly => anomaly.Kind == AnomalyKind.DeduplicationCollapsed);
        Assert.Contains(anomalies, anomaly => anomaly.Kind == AnomalyKind.AddedDataSpike);
        Assert.Contains(anomalies, anomaly => anomaly.Kind == AnomalyKind.ChangedFileSpike);
    }

    [Fact]
    public void TheDetailSaysWhatWasSeenRatherThanWhatItMeans()
    {
        var history = Ordinary(6);
        history.Insert(0, new BackupObservation(Now, "latest", 10_000_000_000, 9_800_000_000, 120_000));

        // Nothing here may name a cause. A video import produces exactly this shape, and telling
        // somebody they have ransomware when they imported holiday footage teaches them to ignore it.
        foreach (var anomaly in BackupAnomalyDetector.Inspect(history))
        {
            Assert.DoesNotContain("ransom", anomaly.Detail, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("attack", anomaly.Detail, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("malware", anomaly.Detail, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void AnOrdinaryNightIsNotUnusual()
    {
        var history = Ordinary(6);
        history.Insert(0, new BackupObservation(Now, "latest", 10_100_000_000, 61_000_000, 47));

        Assert.Empty(BackupAnomalyDetector.Inspect(history));
    }

    [Fact]
    public void OneEarlierSpikeDoesNotHideTheNextOne()
    {
        var history = Ordinary(6);

        // The median, not the mean: a single previous spike would drag an average up far enough for
        // the following one to look ordinary, which is exactly the sequence that matters.
        history.Insert(0, new BackupObservation(Now.AddDays(-1), "spike", 10_000_000_000, 9_000_000_000, 120_000));
        history.Insert(0, new BackupObservation(Now, "latest", 10_000_000_000, 9_000_000_000, 120_000));

        Assert.Contains(
            BackupAnomalyDetector.Inspect(history),
            anomaly => anomaly.Kind == AnomalyKind.AddedDataSpike);
    }

    [Fact]
    public void ARepositoryThatUsuallyWritesNothingProducesNoSpike()
    {
        // An archive that never changes has a median of zero. Every first change would otherwise be
        // infinitely more than usual and report a spike for one added file.
        var history = new List<BackupObservation>
        {
            new(Now, "latest", 1_000_000, BytesAdded: 4_096, FilesChanged: 1),
            new(Now.AddDays(-1), "a", 1_000_000, 0, 0),
            new(Now.AddDays(-2), "b", 1_000_000, 0, 0),
            new(Now.AddDays(-3), "c", 1_000_000, 0, 0),
            new(Now.AddDays(-4), "d", 1_000_000, 0, 0),
            new(Now.AddDays(-5), "e", 1_000_000, 0, 0)
        };

        Assert.DoesNotContain(
            BackupAnomalyDetector.Inspect(history),
            anomaly => anomaly.Kind is AnomalyKind.AddedDataSpike or AnomalyKind.ChangedFileSpike);
    }

    [Fact]
    public void AFirstBackupIsNotADeduplicationCollapse()
    {
        // The first backup of a source is entirely new data, by definition. There is no history to
        // judge it against, and the detector must not treat the beginning as an event.
        var history = new List<BackupObservation>
        {
            new(Now, "first", 10_000_000_000, 10_000_000_000, 50_000)
        };

        Assert.Empty(BackupAnomalyDetector.Inspect(history));
    }

    [Fact]
    public void AnUnusualBackupDoesNotMakeARepositoryLookUnrecoverable()
    {
        var facts = new RepositoryFacts(
            "a",
            "documents",
            LastBackupAt: Now.AddHours(-1),
            LastHealthyCheckAt: Now.AddDays(-1),
            LastProvenRestoreAt: Now.AddDays(-2),
            KitPresent: true,
            StorageImmutable: true);

        var anomalies = BackupAnomalyDetector.Inspect(
        [
            new BackupObservation(Now, "latest", 10_000_000_000, 9_800_000_000, 120_000),
            .. Ordinary(6)
        ]);

        var health = HealthAssessor.Assess(facts, Now, thresholds: null, anomalies);

        // The snapshots are all still there and still restorable. Letting an unusual night lower the
        // verdict would report a recovery problem that does not exist.
        Assert.NotEmpty(anomalies);
        Assert.Equal(HealthVerdict.Recoverable, health.Verdict);
        Assert.Contains(health.Findings, finding => finding.Code == "backup-unusual");
    }
}
