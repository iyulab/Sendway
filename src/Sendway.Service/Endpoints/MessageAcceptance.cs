using Sendway.Core;
using Sendway.Service.Auth;
using Sendway.Service.Delivery;

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
        DeliverySignal signal,
        string channel,
        string recipient,
        string payload)
    {
        string? idempotencyKey = null;
        if (httpContext.Request.Headers.TryGetValue("Idempotency-Key", out var keyValues) &&
            !string.IsNullOrWhiteSpace(keyValues))
        {
            idempotencyKey = keyValues.ToString();
            if (idempotencyKey.Length > MaxIdempotencyKeyLength)
            {
                return Results.BadRequest(new { error = $"Idempotency-Key는 {MaxIdempotencyKeyLength}자를 초과할 수 없습니다." });
            }
        }

        var tenant = httpContext.GetTenant();

        // No cancellation token: a caller that disconnects mid-request must not leave it undecided
        // whether the message was accepted.
        var result = await outbox.EnqueueAsync(new MessageSubmission(tenant.Id, channel, recipient, payload, idempotencyKey));

        if (result.Outcome == EnqueueOutcome.KeyReused)
        {
            return Results.UnprocessableEntity(new { error = "이 Idempotency-Key는 다른 내용의 요청에 이미 사용되었습니다." });
        }

        if (result.Outcome == EnqueueOutcome.Accepted)
        {
            signal.Notify();
        }

        // A replay answers exactly like the original acceptance — same status code, same message
        // ID — with the message's current status.
        return Results.Accepted(
            $"/messages/{result.Record.Id}",
            new SendMessageResponse(result.Record.Id, result.Record.Status.ToString()));
    }
}
