using Fortiq.Application;
using Fortiq.Infrastructure.Keys;
using Fortiq.Monitoring;
using Fortiq.Scheduling;

namespace Fortiq.Operations;

/// <summary>
/// Assembles what this machine knows about its repositories and writes it where a monitoring system
/// can read it, without asking Fortiq anything.
/// </summary>
/// <remarks>
/// The facts come from what already exists: the schedules say which repositories matter, their state
/// says what the last run did, the receipts say what actually happened, and the kit says whether the
/// repository can be opened elsewhere and what its storage promised. Nothing here re-derives health
/// from a belief - if there is no evidence, the report says so.
/// </remarks>
public sealed class HealthPublisher
{
    private readonly IScheduleStore _schedules;
    private readonly string _receiptDirectory;
    private readonly string _reportPath;
    private readonly string _metricsPath;
    private readonly TimeProvider _clock;
    private readonly IStorageProtectionInspector? _protection;
    private readonly RecoveryPhraseRecord? _phrases;

    /// <summary>
    /// The receipts as they were last read, and what the directory looked like when they were.
    /// </summary>
    /// <remarks>
    /// Receipts are append-only and are never edited in place, so a directory holding the same number
    /// of files with the same newest write time holds the same evidence. That makes re-parsing it a
    /// waste, and it was not a small one: the scheduler publishes health at the end of every pass -
    /// once a minute - and nothing ever removes a receipt, so a machine backing up daily with a weekly
    /// drill accumulates roughly five hundred a year per repository and pays for all of them, every
    /// minute, for as long as it is installed. Measured on an ordinary desktop, a thousand receipts
    /// cost about seven tenths of a second per pass and several thousand cost seconds.
    ///
    /// The report itself is still written every pass. Its timestamp is what tells a reader the machine
    /// is alive, and the desktop treats a report older than five minutes as unknown protection, so the
    /// publishing is the point and only the parsing is skipped.
    /// </remarks>
    private volatile ReceiptCache? _cache;

    /// <summary>One read of the receipts, and the directory fingerprint it was read at.</summary>
    private sealed record ReceiptCache(int Files, DateTime Newest, IReadOnlyList<RepositoryEvidence> Evidence);

    public HealthPublisher(
        IScheduleStore schedules,
        string receiptDirectory,
        string reportPath,
        string metricsPath,
        TimeProvider? clock = null,
        IStorageProtectionInspector? protection = null,
        RecoveryPhraseRecord? phrases = null)
    {
        _schedules = schedules ?? throw new ArgumentNullException(nameof(schedules));
        _receiptDirectory = receiptDirectory ?? throw new ArgumentNullException(nameof(receiptDirectory));
        _reportPath = reportPath ?? throw new ArgumentNullException(nameof(reportPath));
        _metricsPath = metricsPath ?? throw new ArgumentNullException(nameof(metricsPath));
        _clock = clock ?? TimeProvider.System;
        _protection = protection;
        _phrases = phrases;
    }

    public async Task<HealthReport> PublishAsync(CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();
        var audit = await Fortiq.Infrastructure.Receipts.AuditLedgerVerifier.VerifyLedgerAsync(_receiptDirectory, null, cancellationToken);
        var auditByRepo = audit.Repositories.ToDictionary(r => r.RepositoryId, StringComparer.OrdinalIgnoreCase);

        var evidence = (await EvidenceAsync(cancellationToken))
            .ToDictionary(entry => entry.RepositoryId, StringComparer.OrdinalIgnoreCase);

        var repositories = new List<RepositoryHealth>();
        foreach (var schedule in await _schedules.ReadSchedulesAsync(cancellationToken))
        {
            var kit = await ReadKitAsync(schedule.KitDirectory, cancellationToken);
            var repositoryId = kit?.Manifest.RepositoryId ?? schedule.Id;
            var state = await _schedules.ReadStateAsync(schedule.Id, cancellationToken);
            evidence.TryGetValue(repositoryId, out var seen);
            var drillFailure = await ReadDrillFailureAsync(schedule, seen?.LastProvenRestoreAt, cancellationToken);

            string? auditLedgerFailure = null;
            if (auditByRepo.TryGetValue(repositoryId, out var repoAudit) && !repoAudit.IsValid)
            {
                var anomaly = repoAudit.Anomalies.Count > 0 ? repoAudit.Anomalies[0] : null;
                auditLedgerFailure = anomaly?.Description ?? "Cryptographic audit chain verification failed";
            }

            // If the audit chain is tampered or broken, receipt evidence cannot be trusted
            var lastBackup = auditLedgerFailure is null ? (seen?.LastBackupAt ?? state.LastSuccessAt) : null;
            var lastCheck = auditLedgerFailure is null ? seen?.LastHealthyCheckAt : null;
            var lastRestore = auditLedgerFailure is null ? seen?.LastProvenRestoreAt : null;

            repositories.Add(HealthAssessor.Assess(
                new RepositoryFacts(
                    repositoryId,
                    schedule.Id,
                    lastBackup,
                    lastCheck,
                    lastRestore,
                    KitPresent: kit is not null,
                    StorageImmutable: kit?.Manifest.StorageProtection?.Immutable ?? false,
                    drillFailure ?? state.LastFailure ?? seen?.LastFailure,
                    await InspectAsync(schedule.RepositoryLocation, cancellationToken),
                    AuditLedgerFailure: auditLedgerFailure,
                    LegacyReceiptCount: auditLedgerFailure is null ? (seen?.LegacyReceiptCount ?? 0) : 0,
                    RecoveryPhrase: await PhraseStateAsync(schedule.Id, cancellationToken)),
                now,
                thresholds: null,
                // Compared against this repository's own history, which is why it is read from the
                // receipts rather than from anything the schedule declares.
                BackupAnomalyDetector.Inspect(auditLedgerFailure is null ? (seen?.Backups ?? []) : [])));
        }

        var report = new HealthReport(now, repositories);
        await HealthPublication.WriteJsonAsync(report, _reportPath, cancellationToken);
        await HealthPublication.WritePrometheusAsync(report, _metricsPath, cancellationToken);
        return report;
    }

    /// <summary>
    /// The receipts, read again only when the directory has changed since the last read.
    /// </summary>
    /// <remarks>
    /// The fingerprint is the file count and the newest write time. A receipt is written once and
    /// never modified, so an addition moves both and a removal moves the count; a change that moved
    /// neither would be a file rewritten in place, which is what the audit ledger exists to catch and
    /// is not something this cache is entitled to hide - <c>AuditLedgerVerifier</c> runs on every pass
    /// regardless of what this returns.
    /// </remarks>
    private async Task<IReadOnlyList<RepositoryEvidence>> EvidenceAsync(CancellationToken cancellationToken)
    {
        var (files, newest) = Fingerprint(_receiptDirectory);
        if (_cache is { } cached && cached.Files == files && cached.Newest == newest)
        {
            return cached.Evidence;
        }

        var evidence = await ReceiptHistory.ReadAsync(_receiptDirectory, cancellationToken);

        // Fingerprinted again after the read rather than before it. A receipt written while the read
        // was in progress must leave a fingerprint that does not match what was just cached, so the
        // next pass reads again instead of remembering a history that was already out of date.
        var after = Fingerprint(_receiptDirectory);

        // No lock: two passes arriving together at worst parse the receipts twice and each publish a
        // correct report, which costs a little work and cannot produce a wrong one. A lock would buy
        // nothing except a field this class would then have to be disposable for.
        _cache = new ReceiptCache(after.Files, after.Newest, evidence);
        return evidence;
    }

    private static (int Files, DateTime Newest) Fingerprint(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return (0, DateTime.MinValue);
        }

        var files = 0;
        var newest = DateTime.MinValue;
        foreach (var path in Directory.EnumerateFiles(directory, "*.json", SearchOption.AllDirectories))
        {
            files++;
            try
            {
                var written = File.GetLastWriteTimeUtc(path);
                if (written > newest)
                {
                    newest = written;
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // A file that cannot be stat-ed makes this fingerprint unusable rather than wrong: the
                // sentinel never equals a real reading, so the next pass reads the receipts again.
                return (-1, DateTime.MinValue);
            }
        }

        return (files, newest);
    }

    /// <summary>
    /// What this machine recorded about whether the recovery phrase was written down.
    /// </summary>
    /// <remarks>
    /// Without a record store the answer is unknown, which says nothing - so a caller that has not
    /// been given one, including every test that predates this, reports exactly what it did before.
    /// </remarks>
    private async Task<RecoveryPhraseState> PhraseStateAsync(string scheduleId, CancellationToken cancellationToken)
    {
        if (_phrases is null)
        {
            return RecoveryPhraseState.Unknown;
        }

        return await _phrases.ReadAsync(scheduleId, cancellationToken) switch
        {
            RecoveryPhraseStatus.Confirmed => RecoveryPhraseState.Confirmed,
            RecoveryPhraseStatus.Issued => RecoveryPhraseState.Issued,
            _ => RecoveryPhraseState.Unknown
        };
    }

    private async Task<string?> ReadDrillFailureAsync(
        BackupSchedule schedule, DateTimeOffset? lastProof, CancellationToken cancellationToken)
    {
        try
        {
            var drill = await _schedules.ReadStateAsync(schedule.DrillStateId, cancellationToken);
            return drill.LastFailure is { Length: > 0 } failure
                && (lastProof is null || drill.LastAttemptAt is null || drill.LastAttemptAt >= lastProof)
                ? "Restore drill failed: " + failure
                : null;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return "Restore drill history could not be read: " + error.Message;
        }
    }

    /// <summary>
    /// Asks the storage what it protects now. Without an inspector, or when the storage cannot be
    /// reached, the answer is unknown - which is reported as unknown rather than resolved to either
    /// claim. A monitoring path that turns "could not ask" into "not protected" cries wolf every time
    /// the network blinks; one that turns it into "protected" is worse, because it goes quiet exactly
    /// when somebody has taken the protection away.
    /// </summary>
    private async Task<StorageProtectionStatus> InspectAsync(string location, CancellationToken cancellationToken)
    {
        if (_protection is null)
        {
            return StorageProtectionStatus.Unknown;
        }

        try
        {
            var protection = await _protection.InspectAsync(location, cancellationToken);
            return protection.Immutable ? StorageProtectionStatus.Immutable : StorageProtectionStatus.NotImmutable;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return StorageProtectionStatus.Unknown;
        }
    }

    private static async Task<OpenedRecoveryKit?> ReadKitAsync(string directory, CancellationToken cancellationToken)
    {
        try
        {
            return await RecoveryKitStore.ReadAsync(directory, cancellationToken);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            // A kit that cannot be read is, for monitoring purposes, a kit that is not there - which
            // is exactly what the report should say.
            return null;
        }
    }
}
