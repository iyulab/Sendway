namespace Sendway.Core;

// One row per accepted send request, from acceptance through delivery. The same row is both the
// durable queue entry the delivery worker claims and the status record callers query afterwards.
// Payload holds the message content, protected at rest.
public sealed class MessageRecord : QueuedDelivery
{
    public required string Channel { get; init; }

    public required string Recipient { get; init; }

    public required DateTimeOffset AcceptedAt { get; init; }

    public string? IdempotencyKey { get; set; }

    // Fingerprint of the request the idempotency key was first used with — a later request reusing
    // the key with different content is a caller error, not a replay.
    public string? RequestHash { get; init; }

    // The caller's deadline: past it the message is not sent (MessageFailureReasons.Expired).
    public DateTimeOffset? ExpiresAt { get; init; }

    // Why the message ended Failed (MessageFailureReasons); null otherwise, and for messages that
    // failed before the reason was recorded.
    public string? FailureReason { get; set; }
}
