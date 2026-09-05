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

    public HealthPublisher(
        IScheduleStore schedules,
        string receiptDirectory,
        string reportPath,
        string metricsPath,
        TimeProvider? clock = null,
        IStorageProtectionInspector? protection = null)
    {
        _schedules = schedules ?? throw new ArgumentNullException(nameof(schedules));
        _receiptDirectory = receiptDirectory ?? throw new ArgumentNullException(nameof(receiptDirectory));
        _reportPath = reportPath ?? throw new ArgumentNullException(nameof(reportPath));
        _metricsPath = metricsPath ?? throw new ArgumentNullException(nameof(metricsPath));
        _clock = clock ?? TimeProvider.System;
        _protection = protection;
    }

    public async Task<HealthReport> PublishAsync(CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();
        var evidence = (await ReceiptHistory.ReadAsync(_receiptDirectory, cancellationToken))
            .ToDictionary(entry => entry.RepositoryId, StringComparer.OrdinalIgnoreCase);

        var repositories = new List<RepositoryHealth>();
        foreach (var schedule in await _schedules.ReadSchedulesAsync(cancellationToken))
        {
            var kit = await ReadKitAsync(schedule.KitDirectory, cancellationToken);
            var repositoryId = kit?.Manifest.RepositoryId ?? schedule.Id;
            var state = await _schedules.ReadStateAsync(schedule.Id, cancellationToken);
            evidence.TryGetValue(repositoryId, out var seen);
            var drillFailure = await ReadDrillFailureAsync(schedule, seen?.LastProvenRestoreAt, cancellationToken);

            repositories.Add(HealthAssessor.Assess(
                new RepositoryFacts(
                    repositoryId,
                    schedule.Id,
                    seen?.LastBackupAt ?? state.LastSuccessAt,
                    seen?.LastHealthyCheckAt,
                    seen?.LastProvenRestoreAt,
                    KitPresent: kit is not null,
                    StorageImmutable: kit?.Manifest.StorageProtection?.Immutable ?? false,
                    drillFailure ?? state.LastFailure ?? seen?.LastFailure,
                    await InspectAsync(schedule.RepositoryLocation, cancellationToken)),
                now,
                thresholds: null,
                // Compared against this repository's own history, which is why it is read from the
                // receipts rather than from anything the schedule declares.
                BackupAnomalyDetector.Inspect(seen?.Backups ?? [])));
        }

        var report = new HealthReport(now, repositories);
        await HealthPublication.WriteJsonAsync(report, _reportPath, cancellationToken);
        await HealthPublication.WritePrometheusAsync(report, _metricsPath, cancellationToken);
        return report;
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
