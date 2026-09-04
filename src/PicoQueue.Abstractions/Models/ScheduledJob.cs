namespace PicoQueue.Abstractions.Models;

public class ScheduledJob : BaseJob
{
    // The exact future timestamp when the job should run (for one-off delayed jobs)
    public DateTimeOffset ScheduledAt { get; set; }

    // Flag to distinguish between a one-off delayed job and a recurring schedule
    public bool IsRecurring { get; set; }

    // Cron expression (e.g. "0 9 * * *"). Required if IsRecurring is true, null otherwise
    public string? CronExpression { get; set; }

    // When this scheduled job last spawned a QueueJob
    public DateTimeOffset? LastTriggeredAt { get; set; }

    // Pre-calculated next eligible run time
    public DateTimeOffset? NextRunAt { get; set; }

    // Allows pausing/resuming a scheduled job without deleting it
    public bool IsEnabled { get; set; } = true;

    public DateTimeOffset? UpdatedAt { get; set; }
}
