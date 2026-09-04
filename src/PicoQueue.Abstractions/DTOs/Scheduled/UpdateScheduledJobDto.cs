namespace PicoQueue.Abstractions.DTOs.Scheduled;

public record UpdateScheduledJobDto
{
    public required Guid JobId { get; init; }
    public string? Name { get; init; }
    public string? Description { get; init; }
    public string? Queue { get; init; }
    public string? PayloadJson { get; init; }
    public string? CronExpression { get; init; }
    public DateTimeOffset? ScheduledAt { get; init; }
    public bool? IsEnabled { get; init; }
}
