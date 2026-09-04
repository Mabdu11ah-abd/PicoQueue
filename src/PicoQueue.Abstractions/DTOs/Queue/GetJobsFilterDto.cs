using PicoQueue.Abstractions.Enums;

namespace PicoQueue.Abstractions.DTOs.Queue;

public record GetJobsFilterDto
{
    public string? Queue { get; init; }
    public JobState? State { get; init; }
    public int PageNumber { get; init; } = 1;
    public int PageSize { get; init; } = 50;
}
