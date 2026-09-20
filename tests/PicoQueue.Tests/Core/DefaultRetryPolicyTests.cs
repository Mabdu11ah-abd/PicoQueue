namespace PicoQueue.Tests.Core;

using PicoQueue.Core.Retry;

public class DefaultRetryPolicyTests
{
    [Fact]
    public void ShouldRetry_WhenAttemptIsLessThanMax_ReturnsTrue()
    {
        var policy = new DefaultRetryPolicy();

        Assert.True(policy.ShouldRetry(1, 3));
        Assert.True(policy.ShouldRetry(2, 3));
    }

    [Fact]
    public void ShouldRetry_WhenAttemptReachesOrExceedsMax_ReturnsFalse()
    {
        var policy = new DefaultRetryPolicy();

        Assert.False(policy.ShouldRetry(3, 3));
        Assert.False(policy.ShouldRetry(4, 3));
    }

    [Fact]
    public void CalculateDelay_AttemptZeroOrNegative_ReturnsZero()
    {
        var policy = new DefaultRetryPolicy();

        Assert.Equal(TimeSpan.Zero, policy.CalculateDelay(0));
        Assert.Equal(TimeSpan.Zero, policy.CalculateDelay(-1));
    }

    [Fact]
    public void CalculateDelay_ExponentialProgression_WithoutJitter()
    {
        // 0 jitter factor for deterministic testing
        var policy = new DefaultRetryPolicy(baseDelay: TimeSpan.FromSeconds(2), maxDelay: TimeSpan.FromHours(1), jitterFactor: 0);

        // Attempt 1: 2 * 2^0 = 2 seconds
        Assert.Equal(TimeSpan.FromSeconds(2), policy.CalculateDelay(1));

        // Attempt 2: 2 * 2^1 = 4 seconds
        Assert.Equal(TimeSpan.FromSeconds(4), policy.CalculateDelay(2));

        // Attempt 3: 2 * 2^2 = 8 seconds
        Assert.Equal(TimeSpan.FromSeconds(8), policy.CalculateDelay(3));

        // Attempt 4: 2 * 2^3 = 16 seconds
        Assert.Equal(TimeSpan.FromSeconds(16), policy.CalculateDelay(4));
    }

    [Fact]
    public void CalculateDelay_ExceedingMaxDelay_CapsAtMaxDelay()
    {
        var maxDelay = TimeSpan.FromSeconds(30);
        var policy = new DefaultRetryPolicy(baseDelay: TimeSpan.FromSeconds(10), maxDelay: maxDelay, jitterFactor: 0);

        // Attempt 5 would normally be 10 * 16 = 160s, capped at 30s
        var delay = policy.CalculateDelay(5);

        Assert.Equal(maxDelay, delay);
    }
}
