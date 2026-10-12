using System.Threading.RateLimiting;
using Sendway.Service.Auth;
using Xunit;

namespace Sendway.Service.Tests;

public class RequestWindowLimiterTests
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly ManualClock _clock = new();

    private static TimeSpan RetryAfterOf(RateLimitLease lease)
    {
        Assert.True(lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter));
        return retryAfter;
    }

    [Fact]
    public void RequestBeyondTheLimit_IsRefusedWithWhatIsLeftOfTheWindow()
    {
        using var limiter = new RequestWindowLimiter(limit: 2, Window, _clock);
        Assert.True(limiter.AttemptAcquire().IsAcquired);
        Assert.True(limiter.AttemptAcquire().IsAcquired);

        _clock.Advance(TimeSpan.FromSeconds(20));
        using var refused = limiter.AttemptAcquire();

        Assert.False(refused.IsAcquired);
        Assert.Equal(TimeSpan.FromSeconds(40), RetryAfterOf(refused));
    }

    [Fact]
    public void WindowReopens_WithTheFirstRequestAfterItClosed()
    {
        using var limiter = new RequestWindowLimiter(limit: 1, Window, _clock);
        Assert.True(limiter.AttemptAcquire().IsAcquired);
        Assert.False(limiter.AttemptAcquire().IsAcquired);

        _clock.Advance(TimeSpan.FromSeconds(90));

        Assert.True(limiter.AttemptAcquire().IsAcquired);
        Assert.Equal(Window, RetryAfterOf(limiter.AttemptAcquire()));
    }

    [Fact]
    public async Task WaitingForAPermit_IsRefusedLikeTryingOnce()
    {
        using var limiter = new RequestWindowLimiter(limit: 1, Window, _clock);
        Assert.True((await limiter.AcquireAsync()).IsAcquired);

        Assert.False((await limiter.AcquireAsync()).IsAcquired);
    }

    [Fact]
    public void IsIdle_OnlyOnceItsWindowHasClosed()
    {
        using var limiter = new RequestWindowLimiter(limit: 1, Window, _clock);
        limiter.AttemptAcquire();
        Assert.Null(limiter.IdleDuration);

        _clock.Advance(TimeSpan.FromSeconds(90));

        Assert.Equal(TimeSpan.FromSeconds(30), limiter.IdleDuration);
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
