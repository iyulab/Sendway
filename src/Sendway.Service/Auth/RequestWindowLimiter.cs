using System.Threading.RateLimiting;

namespace Sendway.Service.Auth;

// Allows a number of requests per window; the window opens with the first request after the
// previous one closed. The framework's fixed window limiter would do, except that a request it
// refuses is told the window's full length rather than what is left of it — and callers are asked
// to wait exactly as long as Retry-After says.
public sealed class RequestWindowLimiter : RateLimiter
{
    private readonly int _limit;
    private readonly TimeSpan _window;
    private readonly TimeProvider _timeProvider;
    private readonly Lock _gate = new();

    private DateTimeOffset _windowStart;
    private int _count;

    public RequestWindowLimiter(int limit, TimeSpan window, TimeProvider timeProvider)
    {
        _limit = limit;
        _window = window;
        _timeProvider = timeProvider;
        _windowStart = timeProvider.GetUtcNow();
    }

    // How long nothing has been counted: the partition owning this limiter drops it once idle.
    public override TimeSpan? IdleDuration
    {
        get
        {
            lock (_gate)
            {
                var sinceWindowClosed = _timeProvider.GetUtcNow() - (_windowStart + _window);
                return sinceWindowClosed > TimeSpan.Zero ? sinceWindowClosed : null;
            }
        }
    }

    public override RateLimiterStatistics? GetStatistics() => null;

    protected override RateLimitLease AttemptAcquireCore(int permitCount)
    {
        lock (_gate)
        {
            var now = _timeProvider.GetUtcNow();
            if (now - _windowStart >= _window)
            {
                _windowStart = now;
                _count = 0;
            }

            if (_count + permitCount > _limit)
            {
                return new Lease(retryAfter: _windowStart + _window - now);
            }

            _count += permitCount;
            return new Lease(retryAfter: null);
        }
    }

    // Requests are refused, never queued, so waiting for a permit is the same as trying once.
    protected override ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken) =>
        ValueTask.FromResult(AttemptAcquireCore(permitCount));

    private sealed class Lease(TimeSpan? retryAfter) : RateLimitLease
    {
        public override bool IsAcquired => retryAfter is null;

        public override IEnumerable<string> MetadataNames => retryAfter is null ? [] : [MetadataName.RetryAfter.Name];

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            if (retryAfter is not null && metadataName == MetadataName.RetryAfter.Name)
            {
                metadata = retryAfter.Value;
                return true;
            }

            metadata = null;
            return false;
        }
    }
}
