namespace PicoQueue.Abstractions.DTOs.Scheduled;

public record CreateScheduledJobDto
{
    public required string TypeName { get; init; }
    public required string PayloadJson { get; init; }
    public string? Name { get; init; }
    public string? Description { get; init; }
    public string Queue { get; init; } = "default";
    public int Priority { get; init; } = 0;
    public int MaxAttempts { get; init; } = 3;

    /// <summary>
    /// When the job should run. For one-off delayed jobs, this is the exact execution time.
    /// For recurring jobs, this can be the initial starting point or first run time.
    /// </summary>
    public DateTimeOffset ScheduledAt { get; init; }

    /// <summary>
    /// Set to true for recurring cron jobs; false for one-off delayed jobs.
    /// </summary>
    public bool IsRecurring { get; init; } = false;

    /// <summary>
    /// Standard cron expression (e.g. "0 9 * * *" or "*/5 * * * *"). Required when IsRecurring is true.
    /// </summary>
    public string? CronExpression { get; init; }

    /// <summary>
    /// Whether the schedule is active upon creation.
    /// </summary>
    public bool IsEnabled { get; init; } = true;
}
