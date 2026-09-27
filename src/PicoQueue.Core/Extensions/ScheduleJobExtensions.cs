namespace PicoQueue.Core.Extensions;

using System.Text.Json;
using PicoQueue.Abstractions.Attributes;
using PicoQueue.Abstractions.DTOs.Scheduled;
using PicoQueue.Abstractions.Interfaces;
using PicoQueue.Abstractions.Models;

/// <summary>
/// Extension methods for <see cref="IScheduleJob"/> supporting strongly-typed delayed and recurring scheduling.
/// </summary>
public static class ScheduleJobExtensions
{
    /// <summary>
    /// Schedules a one-off delayed job to run at a specific future timestamp.
    /// </summary>
    public static Task<ScheduledJob> ScheduleAsync<TJob>(
        this IScheduleJob scheduleJob,
        DateTimeOffset scheduledAt,
        string? queue = null,
        int priority = 0,
        int maxAttempts = 3,
        CancellationToken ct = default)
        where TJob : class, IJob
    {
        return ScheduleCoreAsync<TJob>(
            scheduleJob,
            payloadJson: "{}",
            scheduledAt: scheduledAt,
            isRecurring: false,
            cronExpression: null,
            queue: queue,
            priority: priority,
            maxAttempts: maxAttempts,
            ct: ct);
    }

    /// <summary>
    /// Schedules a one-off delayed job with strongly-typed arguments to run at a specific future timestamp.
    /// </summary>
    public static Task<ScheduledJob> ScheduleAsync<TJob, TArgs>(
        this IScheduleJob scheduleJob,
        TArgs args,
        DateTimeOffset scheduledAt,
        string? queue = null,
        int priority = 0,
        int maxAttempts = 3,
        CancellationToken ct = default)
        where TJob : class, IJob<TArgs>
    {
        return ScheduleCoreAsync<TJob>(
            scheduleJob,
            payloadJson: JsonSerializer.Serialize(args),
            scheduledAt: scheduledAt,
            isRecurring: false,
            cronExpression: null,
            queue: queue,
            priority: priority,
            maxAttempts: maxAttempts,
            ct: ct);
    }

    /// <summary>
    /// Registers a recurring job using a standard cron expression.
    /// </summary>
    public static Task<ScheduledJob> RecurringAsync<TJob>(
        this IScheduleJob scheduleJob,
        string cronExpression,
        string? queue = null,
        int priority = 0,
        int maxAttempts = 3,
        CancellationToken ct = default)
        where TJob : class, IJob
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cronExpression);

        return ScheduleCoreAsync<TJob>(
            scheduleJob,
            payloadJson: "{}",
            scheduledAt: DateTimeOffset.UtcNow,
            isRecurring: true,
            cronExpression: cronExpression,
            queue: queue,
            priority: priority,
            maxAttempts: maxAttempts,
            ct: ct);
    }

    /// <summary>
    /// Registers a recurring job with strongly-typed arguments using a standard cron expression.
    /// </summary>
    public static Task<ScheduledJob> RecurringAsync<TJob, TArgs>(
        this IScheduleJob scheduleJob,
        TArgs args,
        string cronExpression,
        string? queue = null,
        int priority = 0,
        int maxAttempts = 3,
        CancellationToken ct = default)
        where TJob : class, IJob<TArgs>
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cronExpression);

        return ScheduleCoreAsync<TJob>(
            scheduleJob,
            payloadJson: JsonSerializer.Serialize(args),
            scheduledAt: DateTimeOffset.UtcNow,
            isRecurring: true,
            cronExpression: cronExpression,
            queue: queue,
            priority: priority,
            maxAttempts: maxAttempts,
            ct: ct);
    }

    private static Task<ScheduledJob> ScheduleCoreAsync<TJob>(
        IScheduleJob scheduleJob,
        string payloadJson,
        DateTimeOffset scheduledAt,
        bool isRecurring,
        string? cronExpression,
        string? queue,
        int priority,
        int maxAttempts,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scheduleJob);

        var resolvedQueue = queue ?? QueueAttribute.GetQueueName<TJob>("default");

        return scheduleJob.ScheduleAsync(new CreateScheduledJobDto
        {
            Name = typeof(TJob).Name,
            TypeName = typeof(TJob).AssemblyQualifiedName ?? typeof(TJob).FullName!,
            PayloadJson = payloadJson,
            Queue = resolvedQueue,
            Priority = priority,
            MaxAttempts = maxAttempts,
            ScheduledAt = scheduledAt,
            IsRecurring = isRecurring,
            CronExpression = cronExpression
        }, ct);
    }
}
