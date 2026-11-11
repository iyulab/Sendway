namespace Sendway.Core;

// One row per accepted send request, from acceptance through delivery. The same row is both the
// durable queue entry the delivery worker claims and the status record callers query afterwards.
// Payload holds the message content, protected at rest.
public sealed class MessageRecord : QueuedDelivery
{
    public required string Channel { get; init; }

    public required string Recipient { get; init; }

    // The key the per-destination limit counts by: the recipient normalized (an email address in lower case,
    // an SMS number in E.164, a device token as is; one too long for the column, its SHA-256). Null when the
    // message has no single destination to count — an email to several recipients.
    public string? Destination { get; init; }

    public required DateTimeOffset AcceptedAt { get; init; }

    // The order due messages are delivered in, earliest first (earliest deadline first): the message's own
    // deadline (ExpiresAt) when it is nearer than a day, else a day after it was accepted. A code that is
    // worthless in ten minutes therefore goes out before a bulk send accepted earlier, while messages
    // without a deadline keep first-come, first-served order among themselves — and one that has waited a
    // day outranks any newer deadline, so nothing waits forever. Rows from before this existed have the
    // minimum value and so go first, in the order they were due.
    public DateTimeOffset ServeBy { get; init; }

    public string? IdempotencyKey { get; set; }

    // Fingerprint of the request the idempotency key was first used with — a later request reusing
    // the key with different content is a caller error, not a replay.
    public string? RequestHash { get; init; }

    // The caller's deadline: past it the message is not sent (MessageFailureReasons.Expired).
    public DateTimeOffset? ExpiresAt { get; init; }

    // Accepted for a sandbox tenant: delivered by DeliverySimulator, and left out of the operator's
    // health and failure counts.
    public bool Simulated { get; init; }

    // Why the message ended Failed (MessageFailureReasons); null otherwise, and for messages that
    // failed before the reason was recorded.
    public string? FailureReason { get; set; }

    // The recipients of an email the mail server refused for good while accepting the others (JSON,
    // RefusedRecipient); null when there were none.
    public string? RefusedRecipients { get; set; }
}
