using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Caching.Memory;

namespace Sendway.Service.Auth;

// Applied after TenantAuthFilter, before TenantRateLimitFilter — a replayed idempotent request
// shouldn't cost the tenant a rate-limit slot. Scoped per tenant so two different tenants can each
// use "1" as a key without colliding. In-memory only (matches TenantRateLimitFilter's trade-off):
// sufficient for the current single-instance deployment; a redeploy/cold-start loses the cache, but
// that's the same window where an in-flight retry is most likely to matter (the common case is a
// client retrying within seconds of a network blip, not days later).
public sealed class IdempotencyFilter : IEndpointFilter
{
    // Matches the common industry convention for idempotency key length (e.g. Stripe's documented
    // 255-character limit) — long enough for any reasonable client-generated key (UUID, ULID,
    // request hash), short enough to bound how much attacker/client-controlled data can accumulate
    // in the cache (each distinct key held for Ttl).
    private const int MaxKeyLength = 255;

    private static readonly TimeSpan Ttl = TimeSpan.FromHours(24);

    private readonly IMemoryCache _cache;

    public IdempotencyFilter(IMemoryCache cache)
    {
        _cache = cache;
    }

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var httpContext = context.HttpContext;

        if (!httpContext.Request.Headers.TryGetValue("Idempotency-Key", out var keyValues) ||
            string.IsNullOrWhiteSpace(keyValues))
        {
            return await next(context);
        }

        if (keyValues.ToString().Length > MaxKeyLength)
        {
            return Results.BadRequest(new { error = $"Idempotency-Key는 {MaxKeyLength}자를 초과할 수 없습니다." });
        }

        var tenant = httpContext.GetTenant();
        var cacheKey = $"idempotency:{tenant.Id}:{keyValues}";

        if (_cache.TryGetValue(cacheKey, out object? cached))
        {
            return cached;
        }

        var result = await next(context);

        // A 5xx means the send outcome is unknown/didn't happen (e.g. upstream SMTP unreachable) —
        // caching that would permanently block retrying with the same key. 2xx/4xx are deterministic
        // outcomes for this exact request and are safe (and required, for 2xx) to replay verbatim.
        if (result is not IStatusCodeHttpResult { StatusCode: >= 500 })
        {
            _cache.Set(cacheKey, result, Ttl);
        }

        return result;
    }
}
