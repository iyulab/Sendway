using Sendway.Core;
using Microsoft.Extensions.Options;
using Sendway.Service.Auth;

namespace Sendway.Service.Endpoints;

// The part of accepting a send request that is identical for every channel: read the idempotency
// key, store the message, and answer. Channel endpoints validate their own request shape first.
public static class MessageAcceptance
{
    // Matches the common industry convention for idempotency key length (e.g. Stripe's documented
    // 255-character limit) — long enough for any reasonable client-generated key (UUID, ULID,
    // request hash), and the bound the stored column is sized for.
    private const int MaxIdempotencyKeyLength = 255;

    public static async Task<IResult> AcceptAsync(
        HttpContext httpContext,
        IMessageOutbox outbox,
        MessageDeliverySignal signal,
        string channel,
        string recipient,
        string payload,
        DateTimeOffset? expiresAt = null,
        string? destination = null)
    {
        // A deadline already behind us would only produce a message that is never sent.
        if (expiresAt is { } deadline &&
            deadline <= httpContext.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow())
        {
            return ApiProblem.BadRequest("expiresAt must be in the future.");
        }

        string? idempotencyKey = null;
        if (httpContext.Request.Headers.TryGetValue("Idempotency-Key", out var keyValues) &&
            !string.IsNullOrWhiteSpace(keyValues))
        {
            idempotencyKey = keyValues.ToString();
            if (idempotencyKey.Length > MaxIdempotencyKeyLength)
            {
                return ApiProblem.BadRequest($"Idempotency-Key must not exceed {MaxIdempotencyKeyLength} characters.");
            }
        }

        var tenant = httpContext.GetTenant();

        // No cancellation token: a caller that disconnects mid-request must not leave it undecided
        // whether the message was accepted.
        var limits = httpContext.RequestServices.GetRequiredService<IOptions<TenantLimitOptions>>().Value;
        var result = await outbox.EnqueueAsync(new MessageSubmission(
            tenant.Id, channel, recipient, payload, idempotencyKey, expiresAt, tenant.Sandbox, TenantLimits.PerDay(tenant, channel, limits),
            destination, TenantLimits.PerDestinationPerHour(tenant, channel, limits), TenantLimits.DestinationCooldown(tenant, limits)));

        if (result.Outcome == EnqueueOutcome.DestinationLimited)
        {
            return DestinationLimited(httpContext, channel, result.RetryAfter!.Value);
        }

        if (result.Outcome == EnqueueOutcome.DailyLimitReached)
        {
            return DailyLimitReached(httpContext, channel, result.RetryAfter!.Value);
        }

        if (result.Outcome == EnqueueOutcome.ChannelNotConfigured)
        {
            return ChannelNotConfigured(channel);
        }

        if (result.Outcome == EnqueueOutcome.KeyReused)
        {
            return ApiProblem.Create(
                StatusCodes.Status422UnprocessableEntity,
                ApiProblem.IdempotencyKeyReused,
                "This Idempotency-Key was already used for a different request.");
        }

        if (result.Outcome == EnqueueOutcome.Accepted)
        {
            signal.Notify();
        }

        // A replay answers exactly like the original acceptance — same status code, same message
        // ID — with the message's current status.
        var record = result.Record!;
        return Results.Accepted($"/messages/{record.Id}", new SendMessageResponse(record.Id, record.Status.ToString()));
    }

    // The refusals a send and a verification start have in common.
    internal static IResult DailyLimitReached(HttpContext httpContext, string channel, TimeSpan retryAfter)
    {
        SetRetryAfter(httpContext, retryAfter);
        return ApiProblem.Create(
            StatusCodes.Status429TooManyRequests,
            ApiProblem.DailyLimitReached,
            $"This service already sent as many '{channel}' messages in the last 24 hours as it may. Nothing was stored; try again after Retry-After, or ask an administrator to raise the limit.");
    }

    internal static IResult DestinationLimited(HttpContext httpContext, string channel, TimeSpan retryAfter, string? reason = null)
    {
        SetRetryAfter(httpContext, retryAfter);
        return ApiProblem.Create(
            StatusCodes.Status429TooManyRequests,
            ApiProblem.DestinationRateLimited,
            reason ?? $"This service sent to this '{channel}' destination too recently or too often. Nothing was stored; other destinations are not affected. " +
            "Try again after Retry-After, or ask an administrator to change the limit.");
    }

    internal static IResult ChannelNotConfigured(string channel) =>
        ApiProblem.Create(
            StatusCodes.Status409Conflict,
            ApiProblem.ChannelNotConfigured,
            $"Nothing can be sent on the '{channel}' channel for this service yet: no credential for it is registered, " +
            "neither the service's own nor a shared one. Nothing was stored; an administrator has to register one first.");

    private static void SetRetryAfter(HttpContext httpContext, TimeSpan retryAfter) =>
        httpContext.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
}
