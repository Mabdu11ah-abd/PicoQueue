using PicoQueue.Abstractions.Common;
using PicoQueue.Abstractions.DTOs.Queue;
using PicoQueue.Abstractions.Models;

namespace PicoQueue.Abstractions.Interfaces;

public interface IJobQueue
{
    /// <summary>
    /// Enqueues a new job into the queue.
    /// </summary>
    Task<QueueJob> EnqueueAsync(EnqueueJobDto dto, CancellationToken ct = default);

    /// <summary>
    /// Atomically fetches and locks the next eligible job for processing.
    /// Returns null if no eligible job is available.
    /// </summary>
    Task<QueueJob?> DequeueAsync(DequeueJobDto dto, CancellationToken ct = default);

    /// <summary>
    /// Retrieves a queued job by its unique identifier.
    /// </summary>
    Task<QueueJob?> GetByIdAsync(Guid jobId, CancellationToken ct = default);

    /// <summary>
    /// Retrieves a paginated list of queued jobs based on filter criteria.
    /// </summary>
    Task<PagedResult<QueueJob>> GetJobsAsync(GetJobsFilterDto filter, CancellationToken ct = default);

    /// <summary>
    /// Deletes or cancels a job from the queue.
    /// </summary>
    Task<bool> DeleteJobAsync(Guid jobId, CancellationToken ct = default);

    /// <summary>
    /// Re-queues a failed or completed job for another execution attempt.
    /// </summary>
    Task<bool> RetryJobAsync(RetryJobDto dto, CancellationToken ct = default);

    /// <summary>
    /// Marks a job as successfully finished.
    /// </summary>
    Task<bool> AcknowledgeSuccessAsync(Guid jobId, CancellationToken ct = default);

    /// <summary>
    /// Marks a job as failed, optionally scheduling a future retry if attempts remain.
    /// </summary>
    Task<bool> AcknowledgeFailureAsync(Guid jobId, string errorMessage, string? errorDetail = null, DateTimeOffset? retryAt = null, CancellationToken ct = default);

    /// <summary>
    /// Updates the heartbeat timestamp for an actively running job to indicate worker liveness.
    /// </summary>
    Task<bool> UpdateHeartbeatAsync(Guid jobId, CancellationToken ct = default);

    /// <summary>
    /// Recovers jobs left stranded in 'Processing' state due to worker crashes or host restarts.
    /// Returns the number of recovered jobs.
    /// </summary>
    Task<int> RecoverOrphanedJobsAsync(TimeSpan heartbeatTimeout, CancellationToken ct = default);

    /// <summary>
    /// Purges finished jobs (Succeeded, Failed, Deleted, Expired) whose last update timestamp is older than the specified retention duration.
    /// Returns the number of purged jobs.
    /// </summary>
    Task<int> PurgeFinishedJobsAsync(TimeSpan olderThan, CancellationToken ct = default);
}