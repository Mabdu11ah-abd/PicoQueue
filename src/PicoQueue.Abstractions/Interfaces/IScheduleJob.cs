using PicoQueue.Abstractions.Common;
using PicoQueue.Abstractions.DTOs.Scheduled;
using PicoQueue.Abstractions.Models;

namespace PicoQueue.Abstractions.Interfaces;

public interface IScheduleJob
{
    /// <summary>
    /// Creates and registers a new scheduled or recurring job.
    /// </summary>
    Task<ScheduledJob> ScheduleAsync(CreateScheduledJobDto dto, CancellationToken ct = default);

    /// <summary>
    /// Retrieves a scheduled job by its unique identifier.
    /// </summary>
    Task<ScheduledJob?> GetByIdAsync(Guid jobId, CancellationToken ct = default);

    /// <summary>
    /// Retrieves a paginated list of scheduled jobs matching the filter criteria.
    /// </summary>
    Task<PagedResult<ScheduledJob>> GetJobsAsync(GetScheduledJobsFilterDto filter, CancellationToken ct = default);

    /// <summary>
    /// Updates configuration of an existing scheduled job.
    /// </summary>
    Task<bool> UpdateAsync(UpdateScheduledJobDto dto, CancellationToken ct = default);

    /// <summary>
    /// Deletes a scheduled job definition.
    /// </summary>
    Task<bool> DeleteAsync(Guid jobId, CancellationToken ct = default);

    /// <summary>
    /// Enables or pauses a scheduled job without deleting it.
    /// </summary>
    Task<bool> SetEnabledAsync(Guid jobId, bool isEnabled, CancellationToken ct = default);

    /// <summary>
    /// Retrieves all active scheduled jobs that are due for execution (NextRunAt <= now).
    /// Used by the Core scheduler background loop.
    /// </summary>
    Task<IReadOnlyList<ScheduledJob>> GetDueJobsAsync(DateTimeOffset now, CancellationToken ct = default);

    /// <summary>
    /// Records that the scheduled job has triggered, updating the last-triggered timestamp
    /// and advancing NextRunAt to the next occurrence.
    /// </summary>
    Task<bool> RecordTriggerAsync(Guid jobId, DateTimeOffset triggeredAt, DateTimeOffset? nextRunAt, CancellationToken ct = default);
}
