namespace PicoQueue.Core.Retry;

/// <summary>
/// Default retry policy implementing exponential backoff with jitter.
/// Delay = BaseDelay * 2^(attempt - 1) + jitter.
/// </summary>
public class DefaultRetryPolicy : IRetryPolicy
{
    private readonly TimeSpan _baseDelay;
    private readonly TimeSpan _maxDelay;
    private readonly double _jitterFactor;

    public DefaultRetryPolicy(
        TimeSpan? baseDelay = null,
        TimeSpan? maxDelay = null,
        double jitterFactor = 0.2)
    {
        _baseDelay = baseDelay ?? TimeSpan.FromSeconds(2);
        _maxDelay = maxDelay ?? TimeSpan.FromHours(1);
        _jitterFactor = Math.Clamp(jitterFactor, 0.0, 1.0);
    }

    /// <inheritdoc/>
    public bool ShouldRetry(int attempt, int maxAttempts, Exception? exception = null)
    {
        return attempt < maxAttempts;
    }

    /// <inheritdoc/>
    public TimeSpan CalculateDelay(int attempt, Exception? exception = null)
    {
        if (attempt <= 0)
        {
            return TimeSpan.Zero;
        }

        // Exponential backoff: 2^(attempt - 1)
        double exponentialMultiplier = Math.Pow(2, attempt - 1);
        double delaySeconds = _baseDelay.TotalSeconds * exponentialMultiplier;

        // Apply random jitter (+/- jitter factor)
        if (_jitterFactor > 0)
        {
            double jitter = (Random.Shared.NextDouble() * 2.0 - 1.0) * _jitterFactor;
            delaySeconds += delaySeconds * jitter;
        }

        delaySeconds = Math.Max(0, delaySeconds);
        var calculated = TimeSpan.FromSeconds(delaySeconds);

        return calculated > _maxDelay ? _maxDelay : calculated;
    }
}
