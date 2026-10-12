using Microsoft.Extensions.Caching.Memory;
using Sendway.Service.Endpoints;

namespace Sendway.Service.Auth;

// Applies to the /messages group, where authentication has already resolved the tenant.
// In-memory sliding window, scoped per tenant — sufficient for the current single-instance
// deployment (Container Apps maxReplicas: 1). If the deployment ever scales to multiple instances,
// this needs to move to a shared store (e.g. the existing Postgres database) to stay accurate.
public sealed class TenantRateLimitFilter : IEndpointFilter
{
    private readonly IMemoryCache _cache;
    private readonly IConfiguration _configuration;
    private readonly TimeProvider _timeProvider;

    public TenantRateLimitFilter(IMemoryCache cache, IConfiguration configuration, TimeProvider timeProvider)
    {
        _cache = cache;
        _configuration = configuration;
        _timeProvider = timeProvider;
    }

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (!_configuration.GetValue("Sendway:RateLimit:Enabled", true))
        {
            return await next(context);
        }

        var limit = _configuration.GetValue("Sendway:RateLimit:RequestsPerMinute", 60);
        var tenant = context.HttpContext.GetTenant();
        var now = _timeProvider.GetUtcNow();

        var window = _cache.GetOrCreate($"ratelimit:{tenant.Id}", entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(2);
            return new SlidingWindow(now);
        })!;

        if (now - window.Start >= TimeSpan.FromMinutes(1))
        {
            window.Reset(now);
        }

        if (window.Count >= limit)
        {
            var retryAfterSeconds = Math.Max(1, (int)Math.Ceiling((window.Start.AddMinutes(1) - now).TotalSeconds));
            context.HttpContext.Response.Headers["Retry-After"] = retryAfterSeconds.ToString();
            return ApiProblem.Create(
                StatusCodes.Status429TooManyRequests,
                ApiProblem.RateLimited,
                $"The limit of {limit} requests per minute was exceeded. Retry after {retryAfterSeconds} seconds.");
        }

        window.Count++;
        return await next(context);
    }

    private sealed class SlidingWindow(DateTimeOffset start)
    {
        public DateTimeOffset Start { get; private set; } = start;
        public int Count { get; set; }

        public void Reset(DateTimeOffset start)
        {
            Start = start;
            Count = 0;
        }
    }
}
