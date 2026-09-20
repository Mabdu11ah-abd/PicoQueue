namespace PicoQueue.Core.Retry;

/// <summary>
/// Contract for determining job retry eligibility and backoff delays.
/// </summary>
public interface IRetryPolicy
{
    /// <summary>
    /// Determines whether the job should be retried based on current attempt and max attempts.
    /// </summary>
    bool ShouldRetry(int attempt, int maxAttempts, Exception? exception = null);

    /// <summary>
    /// Calculates the backoff delay before the next retry attempt.
    /// </summary>
    TimeSpan CalculateDelay(int attempt, Exception? exception = null);
}
