using System.IO;
using Fortiq.Domain;

namespace Fortiq.Platform.Windows;

public sealed record UsnEvaluationOptions(
    int MaxRecordsToRead = 20000,
    int AnomalyRenameBurstThreshold = 200,
    int AnomalyDeletionBurstThreshold = 200,
    int AnomalySuspiciousExtensionThreshold = 20,
    int RestoreDrillSampleSize = 15);

/// <summary>
/// Evaluates NTFS USN Change Journal continuity, anomalies (e.g. ransomware bursts),
/// and candidates for automated restore drills. Ensures deterministic fallback to
/// FullScanRequired when journal continuity cannot be cryptographically or sequentially proven.
/// </summary>
public static class UsnChangeEvaluator
{
    private static readonly HashSet<string> KnownRansomwareExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".locked", ".crypto", ".locky", ".crypted", ".enc",
        ".wnry", ".ransom", ".crypt", ".darkside", ".blackcat", ".hive"
    };

    public static UsnChangeEvaluationResult Evaluate(
        string volumePath,
        UsnCheckpoint? priorCheckpoint,
        IUsnJournalReader reader,
        UsnEvaluationOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(volumePath);
        ArgumentNullException.ThrowIfNull(reader);

        options ??= new UsnEvaluationOptions();

        if (!reader.IsSupported(volumePath))
        {
            var fallbackCheckpoint = priorCheckpoint ?? new UsnCheckpoint(volumePath, 0, 0, 0, DateTimeOffset.UtcNow);
            return new UsnChangeEvaluationResult(
                FullScanRequired: true,
                FallbackReason: UsnFallbackReason.UnsupportedFilesystem,
                UpdatedCheckpoint: fallbackCheckpoint,
                Changes: [],
                AnomalyVerdict: new UsnAnomalyVerdict(false, 0, 0, 0),
                CandidateDrillSample: []);
        }

        UsnJournalInfo info;
        try
        {
            info = reader.QueryJournal(volumePath);
        }
        catch (UnauthorizedAccessException)
        {
            var fallbackCheckpoint = priorCheckpoint ?? new UsnCheckpoint(volumePath, 0, 0, 0, DateTimeOffset.UtcNow);
            return new UsnChangeEvaluationResult(
                FullScanRequired: true,
                FallbackReason: UsnFallbackReason.AccessDenied,
                UpdatedCheckpoint: fallbackCheckpoint,
                Changes: [],
                AnomalyVerdict: new UsnAnomalyVerdict(false, 0, 0, 0),
                CandidateDrillSample: []);
        }
        catch (Exception)
        {
            var fallbackCheckpoint = priorCheckpoint ?? new UsnCheckpoint(volumePath, 0, 0, 0, DateTimeOffset.UtcNow);
            return new UsnChangeEvaluationResult(
                FullScanRequired: true,
                FallbackReason: UsnFallbackReason.ReadError,
                UpdatedCheckpoint: fallbackCheckpoint,
                Changes: [],
                AnomalyVerdict: new UsnAnomalyVerdict(false, 0, 0, 0),
                CandidateDrillSample: []);
        }

        var nextCheckpoint = new UsnCheckpoint(
            volumePath,
            info.VolumeSerial,
            info.JournalId,
            info.NextUsn,
            DateTimeOffset.UtcNow);

        if (priorCheckpoint is null)
        {
            return new UsnChangeEvaluationResult(
                FullScanRequired: true,
                FallbackReason: UsnFallbackReason.InitialBaseline,
                UpdatedCheckpoint: nextCheckpoint,
                Changes: [],
                AnomalyVerdict: new UsnAnomalyVerdict(false, 0, 0, 0),
                CandidateDrillSample: []);
        }

        if (priorCheckpoint.VolumeSerial != info.VolumeSerial)
        {
            return new UsnChangeEvaluationResult(
                FullScanRequired: true,
                FallbackReason: UsnFallbackReason.VolumeMismatch,
                UpdatedCheckpoint: nextCheckpoint,
                Changes: [],
                AnomalyVerdict: new UsnAnomalyVerdict(false, 0, 0, 0),
                CandidateDrillSample: []);
        }

        if (priorCheckpoint.JournalId != info.JournalId)
        {
            return new UsnChangeEvaluationResult(
                FullScanRequired: true,
                FallbackReason: UsnFallbackReason.JournalRecreated,
                UpdatedCheckpoint: nextCheckpoint,
                Changes: [],
                AnomalyVerdict: new UsnAnomalyVerdict(false, 0, 0, 0),
                CandidateDrillSample: []);
        }

        if (priorCheckpoint.NextUsn < info.LowestValidUsn || priorCheckpoint.NextUsn > info.NextUsn)
        {
            return new UsnChangeEvaluationResult(
                FullScanRequired: true,
                FallbackReason: UsnFallbackReason.JournalTruncated,
                UpdatedCheckpoint: nextCheckpoint,
                Changes: [],
                AnomalyVerdict: new UsnAnomalyVerdict(false, 0, 0, 0),
                CandidateDrillSample: []);
        }

        IReadOnlyList<UsnChangeEntry> changes;
        try
        {
            changes = reader.ReadChanges(volumePath, info.JournalId, priorCheckpoint.NextUsn, options.MaxRecordsToRead);
        }
        catch
        {
            return new UsnChangeEvaluationResult(
                FullScanRequired: true,
                FallbackReason: UsnFallbackReason.ReadError,
                UpdatedCheckpoint: nextCheckpoint,
                Changes: [],
                AnomalyVerdict: new UsnAnomalyVerdict(false, 0, 0, 0),
                CandidateDrillSample: []);
        }

        var anomaly = DetectAnomalies(changes, options);
        var drillCandidates = SelectDrillCandidates(changes, options.RestoreDrillSampleSize);

        return new UsnChangeEvaluationResult(
            FullScanRequired: false,
            FallbackReason: UsnFallbackReason.None,
            UpdatedCheckpoint: nextCheckpoint,
            Changes: changes,
            AnomalyVerdict: anomaly,
            CandidateDrillSample: drillCandidates);
    }

    private static UsnAnomalyVerdict DetectAnomalies(
        IReadOnlyList<UsnChangeEntry> changes,
        UsnEvaluationOptions options)
    {
        int renames = 0;
        int deletions = 0;
        int suspiciousExtensions = 0;

        foreach (var change in changes)
        {
            if (change.IsRenamed)
            {
                renames++;
            }

            if (change.IsDeleted)
            {
                deletions++;
            }

            if (!string.IsNullOrEmpty(change.FileName))
            {
                var ext = Path.GetExtension(change.FileName);
                if (!string.IsNullOrEmpty(ext) && KnownRansomwareExtensions.Contains(ext))
                {
                    suspiciousExtensions++;
                }
            }
        }

        bool isAnomaly = renames >= options.AnomalyRenameBurstThreshold ||
                         deletions >= options.AnomalyDeletionBurstThreshold ||
                         suspiciousExtensions >= options.AnomalySuspiciousExtensionThreshold;

        string? explanation = null;
        if (isAnomaly)
        {
            var reasons = new List<string>();
            if (renames >= options.AnomalyRenameBurstThreshold)
            {
                reasons.Add($"High rename burst ({renames} renames >= threshold {options.AnomalyRenameBurstThreshold})");
            }
            if (deletions >= options.AnomalyDeletionBurstThreshold)
            {
                reasons.Add($"Mass deletion burst ({deletions} deletions >= threshold {options.AnomalyDeletionBurstThreshold})");
            }
            if (suspiciousExtensions >= options.AnomalySuspiciousExtensionThreshold)
            {
                reasons.Add($"Known ransomware file extensions detected ({suspiciousExtensions} >= threshold {options.AnomalySuspiciousExtensionThreshold})");
            }
            explanation = string.Join("; ", reasons);
        }

        return new UsnAnomalyVerdict(isAnomaly, renames, deletions, suspiciousExtensions, explanation);
    }

    private static List<string> SelectDrillCandidates(
        IReadOnlyList<UsnChangeEntry> changes,
        int sampleSize)
    {
        if (sampleSize <= 0)
        {
            return [];
        }

        var candidates = new List<string>();
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Pick distinct modified or created files that have a valid extension and aren't temporary
        foreach (var change in changes)
        {
            if (string.IsNullOrWhiteSpace(change.FileName)) continue;
            if (change.FileName.StartsWith('~') || change.FileName.StartsWith('.')) continue;

            var ext = Path.GetExtension(change.FileName);
            if (string.IsNullOrEmpty(ext) || ext.Equals(".tmp", StringComparison.OrdinalIgnoreCase) || ext.Equals(".log", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if ((change.IsDataModified || change.IsCreated) && seenNames.Add(change.FileName))
            {
                candidates.Add(change.FileName);
                if (candidates.Count >= sampleSize)
                {
                    break;
                }
            }
        }

        return candidates;
    }
}
