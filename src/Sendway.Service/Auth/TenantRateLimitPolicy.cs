using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Sendway.Service.Endpoints;

namespace Sendway.Service.Auth;

// Limits each tenant to a number of requests per minute on the /messages group. It runs in the
// rate limiting middleware, after authentication has resolved the tenant and before the endpoint
// reads the request body.
//
// The counters live in this process's memory — sufficient for a single-instance deployment. If the
// deployment ever scales to multiple instances, this needs a shared store to stay accurate.
public sealed class TenantRateLimitPolicy : IRateLimiterPolicy<Guid>
{
    public const string PolicyName = "tenant";

    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly IConfiguration _configuration;
    private readonly TimeProvider _timeProvider;

    public TenantRateLimitPolicy(IConfiguration configuration, TimeProvider timeProvider)
    {
        _configuration = configuration;
        _timeProvider = timeProvider;
    }

    private int RequestsPerMinute => _configuration.GetValue("Sendway:RateLimit:RequestsPerMinute", 60);

    public RateLimitPartition<Guid> GetPartition(HttpContext httpContext)
    {
        var tenantId = httpContext.GetTenant().Id;

        if (!_configuration.GetValue("Sendway:RateLimit:Enabled", true))
        {
            return RateLimitPartition.GetNoLimiter(tenantId);
        }

        return RateLimitPartition.Get(tenantId, _ => new RequestWindowLimiter(RequestsPerMinute, Window, _timeProvider));
    }

    public Func<OnRejectedContext, CancellationToken, ValueTask> OnRejected => async (context, _) =>
    {
        var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var remaining) ? remaining : Window;
        var retryAfterSeconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));

        context.HttpContext.Response.Headers.RetryAfter = retryAfterSeconds.ToString();
        await ApiProblem.Create(
                StatusCodes.Status429TooManyRequests,
                ApiProblem.RateLimited,
                $"The limit of {RequestsPerMinute} requests per minute was exceeded. Retry after {retryAfterSeconds} seconds.")
            .ExecuteAsync(context.HttpContext);
    };
}
