namespace Sendway.Core;

// One row per accepted send request, from acceptance through delivery. The same row is both the
// durable queue entry the delivery worker claims and the status record callers query afterwards.
public sealed class MessageRecord
{
    public required Guid Id { get; init; }

    public required Guid TenantId { get; init; }

    public required string Channel { get; init; }

    public required string Recipient { get; init; }

    public required MessageDeliveryStatus Status { get; set; }

    // The most recent attempt's failure. Kept while the message waits for a retry, so a caller
    // polling a still-Queued message can see why it hasn't gone out yet.
    public string? Error { get; set; }

    public required DateTimeOffset AcceptedAt { get; init; }

    // Set once, when the message reaches Sent or Failed.
    public DateTimeOffset? CompletedAt { get; set; }

    // Incremented when an attempt is claimed, so it also serves as the optimistic-concurrency token
    // that keeps two workers from claiming (or completing) the same attempt.
    public int AttemptCount { get; set; }

    // When the message next becomes claimable. While Queued this is the retry schedule; while
    // Sending it is the lease expiry — a worker that dies mid-attempt leaves the row claimable again
    // once it passes. Null for terminal messages.
    public DateTimeOffset? NextAttemptAt { get; set; }

    // The message content, protected at rest, needed only until delivery. Cleared when the message
    // reaches a terminal state so content isn't retained beyond its purpose.
    public string? Payload { get; set; }

    public string? IdempotencyKey { get; set; }

    // Fingerprint of the request the idempotency key was first used with — a later request reusing
    // the key with different content is a caller error, not a replay.
    public string? RequestHash { get; init; }
}
