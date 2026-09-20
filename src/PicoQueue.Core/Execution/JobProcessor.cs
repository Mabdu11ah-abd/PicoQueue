namespace PicoQueue.Core.Execution;

using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PicoQueue.Abstractions.DTOs.Execution;
using PicoQueue.Abstractions.DTOs.Queue;
using PicoQueue.Abstractions.Interfaces;
using PicoQueue.Abstractions.Models;
using PicoQueue.Core.Options;
using PicoQueue.Core.Retry;

public class JobProcessor : BackgroundService
{
    private readonly IJobFactory _jobFactory;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SemaphoreSlim _concurrencySemaphore;
    private readonly PicoQueueOptions _options;
    private readonly ILogger<JobProcessor> _logger;
    private readonly IJobQueue _jobQueue;
    private readonly IRetryPolicy _retryPolicy;
    private readonly string _workerId = $"worker-{Environment.MachineName}-{Guid.NewGuid():N}";

    public JobProcessor(
        IJobQueue jobQueue,
        IJobFactory jobFactory,
        IServiceScopeFactory scopeFactory,
        IOptions<PicoQueueOptions> options,
        ILogger<JobProcessor> logger,
        IRetryPolicy? retryPolicy = null)
    {
        _jobQueue = jobQueue ?? throw new ArgumentNullException(nameof(jobQueue));
        _jobFactory = jobFactory ?? throw new ArgumentNullException(nameof(jobFactory));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _retryPolicy = retryPolicy ?? new DefaultRetryPolicy();
        _concurrencySemaphore = new SemaphoreSlim(_options.MaxConcurrentJobs, _options.MaxConcurrentJobs);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var acquired = false;
            try
            {
                await _concurrencySemaphore.WaitAsync(stoppingToken);
                acquired = true;

                QueueJob? job = await _jobQueue.DequeueAsync(new DequeueJobDto
                {
                    WorkerId = _workerId,
                    Queue = "default"
                }, stoppingToken);

                if (job is null)
                {
                    acquired = false;
                    _concurrencySemaphore.Release();
                    await Task.Delay(_options.QueuePollingInterval, stoppingToken);
                    continue;
                }

                // Ownership of the acquired slot transfers to ProcessJobWithConcurrencyAsync
                acquired = false;
                _ = ProcessJobWithConcurrencyAsync(job, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                if (acquired)
                {
                    _concurrencySemaphore.Release();
                }
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error while dequeuing next job.");
                if (acquired)
                {
                    _concurrencySemaphore.Release();
                }
                await Task.Delay(_options.QueuePollingInterval, stoppingToken);
            }
        }
    }

    private async Task ProcessJobAsync(QueueJob job, CancellationToken stoppingToken)
    {
        using var scope = _scopeFactory.CreateScope();
        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

        // Start periodic heartbeat updates while job executes
        var heartbeatTask = RunHeartbeatLoopAsync(job.Id, heartbeatCts.Token);

        try
        {
            var jobType = Type.GetType(job.TypeName);
            if (jobType is null)
            {
                throw new InvalidOperationException($"Could not resolve job type '{job.TypeName}'. Ensure assembly is loaded.");
            }

            var jobInstance = _jobFactory.CreateJob(scope.ServiceProvider, jobType);

            var context = new JobExecutionContext
            {
                JobId = job.Id,
                Queue = job.Queue,
                AttemptNumber = job.Attempt + 1,
                MaxAttempts = job.MaxAttempts,
                CreatedAt = job.CreatedAt,
                ScheduledJobId = job.ScheduledJobId
            };

            _logger.LogInformation("Processing job {JobId} ({JobName}) [Attempt {Attempt}/{MaxAttempts}] on worker {WorkerId}...",
                job.Id, job.Name, context.AttemptNumber, context.MaxAttempts, _workerId);

            var genericJobInterface = jobType.GetInterfaces()
                .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IJob<>));

            if (genericJobInterface != null)
            {
                var argType = genericJobInterface.GetGenericArguments()[0];
                var args = JsonSerializer.Deserialize(job.PayloadJson, argType);
                var method = genericJobInterface.GetMethod(nameof(IJob<object>.ExecuteAsync))!;

                var task = (Task)method.Invoke(jobInstance, [args, context, stoppingToken])!;
                await task;
            }
            else if (jobInstance is IJob nonGenericJob)
            {
                await nonGenericJob.ExecuteAsync(context, stoppingToken);
            }
            else
            {
                throw new InvalidOperationException($"Type '{jobType.Name}' does not implement IJob or IJob<TArgs>.");
            }

            await _jobQueue.AcknowledgeSuccessAsync(job.Id, stoppingToken);
            _logger.LogInformation("Successfully completed job {JobId} ({JobName}).", job.Id, job.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error while executing job {JobId}", job.Id);
            await HandleRetryAsync(job, ex, stoppingToken);
        }
        finally
        {
            heartbeatCts.Cancel();
            try
            {
                await heartbeatTask;
            }
            catch (OperationCanceledException)
            {
                // Expected when canceling heartbeat
            }
        }
    }

    private async Task RunHeartbeatLoopAsync(Guid jobId, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        try
        {
            while (!ct.IsCancellationRequested && await timer.WaitForNextTickAsync(ct))
            {
                await _jobQueue.UpdateHeartbeatAsync(jobId, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on task completion
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update heartbeat for job {JobId}", jobId);
        }
    }

    private async Task HandleRetryAsync(QueueJob job, Exception ex, CancellationToken ct)
    {
        int nextAttempt = job.Attempt + 1;
        DateTimeOffset? retryAt = null;

        if (_retryPolicy.ShouldRetry(nextAttempt, job.MaxAttempts, ex))
        {
            var delay = _retryPolicy.CalculateDelay(nextAttempt, ex);
            retryAt = DateTimeOffset.UtcNow.Add(delay);

            _logger.LogWarning(
                ex,
                "Job {JobId} failed on attempt {Attempt}/{MaxAttempts}. Scheduled retry at {RetryAt}",
                job.Id,
                nextAttempt,
                job.MaxAttempts,
                retryAt);
        }
        else
        {
            _logger.LogError(
                ex,
                "Job {JobId} failed permanently after {MaxAttempts} attempts.",
                job.Id,
                job.MaxAttempts);
        }

        await _jobQueue.AcknowledgeFailureAsync(
            job.Id,
            errorMessage: ex.Message,
            errorDetail: ex.ToString(),
            retryAt: retryAt,
            ct: ct);
    }

    private async Task ProcessJobWithConcurrencyAsync(QueueJob job, CancellationToken stoppingToken)
    {
        try
        {
            await ProcessJobAsync(job, stoppingToken);
        }
        finally
        {
            _concurrencySemaphore.Release();
        }
    }
}