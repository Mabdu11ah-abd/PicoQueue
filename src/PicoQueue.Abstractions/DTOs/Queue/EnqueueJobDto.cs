namespace PicoQueue.Abstractions.DTOs.Queue;

public record EnqueueJobDto
{
    public required string TypeName { get; init; }
    public required string PayloadJson { get; init; }
    public string? Name { get; init; }
    public string? Description { get; init; }
    public string Queue { get; init; } = "default";
    public int Priority { get; init; } = 0;
    public int MaxAttempts { get; init; } = 3;
}
