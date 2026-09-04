namespace PicoQueue.Abstractions.DTOs.Scheduled;

public record GetScheduledJobsFilterDto
{
    public string? Queue { get; init; }
    public bool? IsEnabled { get; init; }
    public bool? IsRecurring { get; init; }
    public int PageNumber { get; init; } = 1;
    public int PageSize { get; init; } = 50;
}
