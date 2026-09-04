namespace PicoQueue.Abstractions.DTOs.Execution;

public record JobExecutionContext
{
    /// <summary>
    /// Unique identifier of the queued job being executed.
    /// </summary>
    public required Guid JobId { get; init; }

    /// <summary>
    /// Name of the queue this job was dispatched from.
    /// </summary>
    public required string Queue { get; init; }

    /// <summary>
    /// Current attempt count (1 for first run, 2 for second run, etc.).
    /// </summary>
    public int AttemptNumber { get; init; }

    /// <summary>
    /// Maximum retry attempts configured for this job.
    /// </summary>
    public int MaxAttempts { get; init; }

    /// <summary>
    /// When this job was originally enqueued.
    /// </summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// The scheduled job ID if this job was triggered by a schedule.
    /// </summary>
    public Guid? ScheduledJobId { get; init; }
}
