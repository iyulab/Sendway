namespace Sendway.Core;

// What every durably queued piece of outbound work has in common, whichever queue it sits in: who it
// belongs to, where it is in its lifecycle, and when it may next be attempted. The claim/lease
// mechanics (DeliveryQueue) are written once against this shape.
public abstract class QueuedDelivery
{
    public required Guid Id { get; init; }

    public required Guid TenantId { get; init; }

    public required DeliveryStatus Status { get; set; }

    // The most recent attempt's failure. Kept while the item waits for a retry, so anyone looking at
    // a still-Queued item can see why it hasn't gone out yet.
    public string? Error { get; set; }

    // Set once, when the item reaches Sent or Failed.
    public DateTimeOffset? CompletedAt { get; set; }

    // Incremented when an attempt is claimed, so it also serves as the optimistic-concurrency token
    // that keeps two workers from claiming (or completing) the same attempt.
    public int AttemptCount { get; set; }

    // When the item next becomes claimable. While Queued this is the retry schedule; while Sending it
    // is the lease expiry — a worker that dies mid-attempt leaves the row claimable again once it
    // passes. Null for terminal items.
    public DateTimeOffset? NextAttemptAt { get; set; }

    // What is to be delivered. Needed only until delivery, and cleared when the item reaches a
    // terminal state so content isn't retained beyond its purpose.
    public string? Payload { get; set; }
}
