namespace PicoQueue.Abstractions.DTOs.Queue;

public record DequeueJobDto
{
    public required string WorkerId { get; init; }
    public string Queue { get; init; } = "default";
}
