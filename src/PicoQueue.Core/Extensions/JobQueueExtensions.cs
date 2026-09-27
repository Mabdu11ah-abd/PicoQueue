namespace PicoQueue.Core.Extensions;

using System.Text.Json;
using PicoQueue.Abstractions.Attributes;
using PicoQueue.Abstractions.DTOs.Queue;
using PicoQueue.Abstractions.Interfaces;
using PicoQueue.Abstractions.Models;

/// <summary>
/// Extension methods for <see cref="IJobQueue"/> supporting strongly-typed enqueueing
/// and automatic resolution of queue names from <see cref="QueueAttribute"/>.
/// </summary>
public static class JobQueueExtensions
{
    /// <summary>
    /// Enqueues a fire-and-forget job without arguments, resolving the queue from [Queue] attribute if present.
    /// </summary>
    public static Task<QueueJob> EnqueueAsync<TJob>(
        this IJobQueue jobQueue,
        string? queue = null,
        int priority = 0,
        int maxAttempts = 3,
        CancellationToken ct = default)
        where TJob : class, IJob
    {
        ArgumentNullException.ThrowIfNull(jobQueue);

        var resolvedQueue = queue ?? QueueAttribute.GetQueueName<TJob>("default");

        return jobQueue.EnqueueAsync(new EnqueueJobDto
        {
            Name = typeof(TJob).Name,
            TypeName = typeof(TJob).AssemblyQualifiedName ?? typeof(TJob).FullName!,
            PayloadJson = "{}",
            Queue = resolvedQueue,
            Priority = priority,
            MaxAttempts = maxAttempts
        }, ct);
    }

    /// <summary>
    /// Enqueues a fire-and-forget job with strongly-typed arguments, resolving the queue from [Queue] attribute if present.
    /// </summary>
    public static Task<QueueJob> EnqueueAsync<TJob, TArgs>(
        this IJobQueue jobQueue,
        TArgs args,
        string? queue = null,
        int priority = 0,
        int maxAttempts = 3,
        CancellationToken ct = default)
        where TJob : class, IJob<TArgs>
    {
        ArgumentNullException.ThrowIfNull(jobQueue);

        var resolvedQueue = queue ?? QueueAttribute.GetQueueName<TJob>("default");

        return jobQueue.EnqueueAsync(new EnqueueJobDto
        {
            Name = typeof(TJob).Name,
            TypeName = typeof(TJob).AssemblyQualifiedName ?? typeof(TJob).FullName!,
            PayloadJson = JsonSerializer.Serialize(args),
            Queue = resolvedQueue,
            Priority = priority,
            MaxAttempts = maxAttempts
        }, ct);
    }
}
