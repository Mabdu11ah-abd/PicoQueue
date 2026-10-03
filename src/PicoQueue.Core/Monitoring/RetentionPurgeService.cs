namespace PicoQueue.Core.Monitoring;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PicoQueue.Abstractions.Interfaces;
using PicoQueue.Core.Options;

/// <summary>
/// Background hosted service that periodically purges finished jobs
/// (Succeeded, Failed, Deleted, Expired) whose last update exceeds the retention period.
/// </summary>
public class RetentionPurgeService : BackgroundService
{
    private readonly IJobQueue _jobQueue;
    private readonly PicoQueueOptions _options;
    private readonly ILogger<RetentionPurgeService> _logger;

    public RetentionPurgeService(
        IJobQueue jobQueue,
        IOptions<PicoQueueOptions> options,
        ILogger<RetentionPurgeService> logger)
    {
        _jobQueue = jobQueue ?? throw new ArgumentNullException(nameof(jobQueue));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_options.RetentionPurgeInterval <= TimeSpan.Zero)
        {
            _logger.LogInformation("Automatic database retention purge is disabled (RetentionPurgeInterval <= 0).");
            return;
        }

        using var timer = new PeriodicTimer(_options.RetentionPurgeInterval);

        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                int purgedCount = await _jobQueue.PurgeFinishedJobsAsync(_options.RetentionPeriod, stoppingToken);
                if (purgedCount > 0)
                {
                    _logger.LogInformation("RetentionPurgeService purged {PurgedCount} finished job(s) older than {RetentionPeriod}.", purgedCount, _options.RetentionPeriod);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred while purging finished jobs in RetentionPurgeService.");
            }
        }
    }
}
