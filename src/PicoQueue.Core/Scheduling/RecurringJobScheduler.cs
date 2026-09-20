namespace PicoQueue.Core.Scheduling;

using Cronos;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PicoQueue.Abstractions.DTOs.Queue;
using PicoQueue.Abstractions.Interfaces;
using PicoQueue.Core.Options;

public class RecurringJobScheduler : BackgroundService
{
    private readonly IJobQueue _jobQueue;
    private readonly IScheduleJob _scheduleJob;
    private readonly PicoQueueOptions _options;
    private readonly ILogger<RecurringJobScheduler> _logger;

    public RecurringJobScheduler(
        IJobQueue jobQueue,
        IScheduleJob scheduleJob,
        IOptions<PicoQueueOptions> options,
        ILogger<RecurringJobScheduler> logger)
    {
        _jobQueue = jobQueue ?? throw new ArgumentNullException(nameof(jobQueue));
        _scheduleJob = scheduleJob ?? throw new ArgumentNullException(nameof(scheduleJob));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_options.SchedulePollingInterval);

        // Run an immediate check on startup, then continue with timer
        do
        {
            try
            {
                var now = DateTimeOffset.UtcNow;
                var dueJobs = await _scheduleJob.GetDueJobsAsync(now, stoppingToken);

                foreach (var job in dueJobs)
                {
                    try
                    {
                        // 1. Enqueue execution item into ready queue
                        await _jobQueue.EnqueueAsync(new EnqueueJobDto
                        {
                            Name = job.Name,
                            Description = job.Description,
                            TypeName = job.TypeName,
                            PayloadJson = job.PayloadJson,
                            Queue = job.Queue,
                            Priority = 0,
                            MaxAttempts = 3
                        }, stoppingToken);

                        // 2. Calculate next run time if recurring
                        DateTimeOffset? nextRunAt = null;
                        if (job.IsRecurring && !string.IsNullOrWhiteSpace(job.CronExpression))
                        {
                            try
                            {
                                var cron = CronExpression.Parse(job.CronExpression);
                                nextRunAt = cron.GetNextOccurrence(now, TimeZoneInfo.Utc);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Failed to parse CronExpression '{Cron}' for job {JobId}", job.CronExpression, job.Id);
                            }
                        }

                        // 3. Atomically record trigger and update next run time
                        await _scheduleJob.RecordTriggerAsync(job.Id, now, nextRunAt, stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to process due scheduled job {JobId}", job.Id);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error while executing recurring job scheduler pass.");
            }
        } while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken));
    }
}
