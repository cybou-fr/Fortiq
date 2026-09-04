using Fortiq.Operations;
using Fortiq.Monitoring;
using Fortiq.Scheduling;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Fortiq.Service;

/// <summary>How often the service looks for work, and where its state lives.</summary>
public sealed record SchedulerOptions(TimeSpan PollInterval)
{
    public static SchedulerOptions Default { get; } = new(TimeSpan.FromMinutes(1));
}

/// <summary>
/// Asks the runner what is due, at a steady tick. The tick is not the schedule: it decides how
/// promptly a due backup starts, while whether one is due is the schedule's own business.
/// </summary>
/// <remarks>
/// The loop survives whatever a pass throws. A service that stopped on the first failure would take
/// every other schedule down with the one that broke, and would stop backing anything up until
/// someone noticed.
/// </remarks>
public sealed partial class SchedulerWorker : BackgroundService
{
    private readonly ScheduledBackupRunner _runner;
    private readonly SchedulerOptions _options;
    private readonly ILogger<SchedulerWorker> _logger;
    private readonly TimeProvider _clock;
    private readonly HealthPublisher? _health;

    public SchedulerWorker(
        ScheduledBackupRunner runner,
        SchedulerOptions options,
        ILogger<SchedulerWorker> logger,
        TimeProvider? clock = null,
        HealthPublisher? health = null)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _clock = clock ?? TimeProvider.System;
        _health = health;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        SchedulerStarted(_logger, _options.PollInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            await RunOnePassAsync(stoppingToken);

            try
            {
                await Task.Delay(_options.PollInterval, _clock, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        SchedulerStopped(_logger);
    }

    internal async Task RunOnePassAsync(CancellationToken stoppingToken)
    {
        try
        {
            foreach (var outcome in await _runner.RunDueAsync(stoppingToken))
            {
                if (outcome.Failure is not null)
                {
                    ScheduleFailed(_logger, outcome.ScheduleId, outcome.Failure);
                }
                else if (outcome.SnapshotId is not null)
                {
                    ScheduleSucceeded(_logger, outcome.ScheduleId, outcome.SnapshotId);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            // Reading the schedules themselves can fail - a malformed file, an unreachable directory
            // - and that must not end the service.
            PassFailed(_logger, error);
        }

        await PublishHealthAsync(stoppingToken);
    }

    /// <summary>
    /// Publishes health after every pass, including a pass that failed. A monitoring path that only
    /// reports when things go well is worse than none: it goes quiet exactly when it matters.
    /// </summary>
    private async Task PublishHealthAsync(CancellationToken stoppingToken)
    {
        if (_health is null)
        {
            return;
        }

        try
        {
            var report = await _health.PublishAsync(stoppingToken);
            if (report.Worst != HealthVerdict.Recoverable)
            {
                foreach (var repository in report.Repositories.Where(entry => entry.Verdict != HealthVerdict.Recoverable))
                {
                    HealthConcern(
                        _logger,
                        repository.ScheduleId ?? repository.RepositoryId,
                        repository.Verdict.ToString(),
                        string.Join("; ", repository.Findings.Select(finding => finding.Code)));
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            HealthPublicationFailed(_logger, error);
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Fortiq scheduler started; polling every {interval}.")]
    private static partial void SchedulerStarted(ILogger logger, TimeSpan interval);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Fortiq scheduler stopped.")]
    private static partial void SchedulerStopped(ILogger logger);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "Schedule {schedule} produced snapshot {snapshot}.")]
    private static partial void ScheduleSucceeded(ILogger logger, string schedule, string snapshot);

    [LoggerMessage(EventId = 4, Level = LogLevel.Error, Message = "Schedule {schedule} failed: {failure}")]
    private static partial void ScheduleFailed(ILogger logger, string schedule, string failure);

    [LoggerMessage(EventId = 5, Level = LogLevel.Error, Message = "A scheduling pass failed.")]
    private static partial void PassFailed(ILogger logger, Exception error);

    [LoggerMessage(EventId = 6, Level = LogLevel.Warning, Message = "{schedule} is {verdict}: {findings}")]
    private static partial void HealthConcern(ILogger logger, string schedule, string verdict, string findings);

    [LoggerMessage(EventId = 7, Level = LogLevel.Error, Message = "Publishing the health report failed.")]
    private static partial void HealthPublicationFailed(ILogger logger, Exception error);
}
