using PicoQueue.Abstractions.Enums;

namespace PicoQueue.Abstractions.Models;

public class QueueJob : BaseJob
{
    public JobState State { get; set; } = JobState.Enqueued;
    public int Priority;
    
    // Execution lifecycle timestamps
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? HeartbeatAt { get; set; }

    // Retry tracking
    public int Attempt { get; set; } = 0;
    public int MaxAttempts { get; set; } = 3;
    public DateTimeOffset? RetryAt { get; set; }

    // Diagnostics and ownership
    public string? ErrorMessage { get; set; }
    public string? ErrorDetail { get; set; }
    public string? WorkerId { get; set; }

    // Reference back if this QueueJob was spawned by a ScheduledJob
    public Guid? ScheduledJobId { get; set; }
    
}
