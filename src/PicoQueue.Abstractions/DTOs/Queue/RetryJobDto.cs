namespace PicoQueue.Abstractions.DTOs.Queue;

public record RetryJobDto
{
    public required Guid JobId { get; init; }
    public bool ResetAttemptCount { get; init; } = true;
}
