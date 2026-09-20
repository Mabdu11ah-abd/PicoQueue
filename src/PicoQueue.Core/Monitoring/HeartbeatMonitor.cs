namespace PicoQueue.Core.Monitoring;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PicoQueue.Abstractions.Interfaces;
using PicoQueue.Core.Options;

/// <summary>
/// Background service that periodically detects crashed or orphaned workers
/// by inspecting heartbeat timestamps on actively 'Processing' jobs.
/// </summary>
public class HeartbeatMonitor : BackgroundService
{
    private readonly IJobQueue _jobQueue;
    private readonly PicoQueueOptions _options;
    private readonly ILogger<HeartbeatMonitor> _logger;

    public HeartbeatMonitor(
        IJobQueue jobQueue,
        IOptions<PicoQueueOptions> options,
        ILogger<HeartbeatMonitor> logger)
    {
        _jobQueue = jobQueue ?? throw new ArgumentNullException(nameof(jobQueue));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_options.HeartBeatMonitorInterval);

        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                // Timeout threshold: 2x the monitor interval or a minimum of 1 minute
                var timeout = _options.HeartBeatMonitorInterval * 2;
                if (timeout < TimeSpan.FromMinutes(1))
                {
                    timeout = TimeSpan.FromMinutes(1);
                }

                // If the underlying queue implementation supports orphaned job recovery via reflection/dynamic dispatch
                // (e.g. SqliteJobQueue.RecoverOrphanedJobsAsync)
                var method = _jobQueue.GetType().GetMethod("RecoverOrphanedJobsAsync", [typeof(TimeSpan), typeof(CancellationToken)]);
                if (method != null)
                {
                    var task = (Task<int>)method.Invoke(_jobQueue, [timeout, stoppingToken])!;
                    int recovered = await task;
                    if (recovered > 0)
                    {
                        _logger.LogWarning("HeartbeatMonitor recovered {RecoveredCount} orphaned job(s) whose heartbeats timed out.", recovered);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error while checking for orphaned jobs in HeartbeatMonitor.");
            }
        }
    }
}
