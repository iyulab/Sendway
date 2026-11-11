namespace Sendway.Core;

public interface IMessageOutbox : IDeliveryQueue<ClaimedMessage>
{
    // Durably accepts a message for delivery. When an idempotency key is given, a second call with
    // the same tenant and key never creates a second message — including while the first is still
    // being delivered.
    Task<EnqueueResult> EnqueueAsync(MessageSubmission submission, CancellationToken cancellationToken = default);

    Task<MessageRecord?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    // How each channel with unfinished messages is faring — only channels where something is wrong
    // are listed. See ChannelDeliveryState.
    Task<IReadOnlyList<ChannelDeliveryState>> GetChannelStatesAsync(CancellationToken cancellationToken = default);

    // The claimed message's content, decrypted. Throws when it cannot be — part of the attempt, so
    // the failure is retried and counted like any other rather than stopping the claim.
    string ReadPayload(ClaimedMessage message);

    // Records, for the attempt that holds the message, the recipients refused while the others were
    // accepted — before the attempt is marked Sent, so the outcome event carries them.
    Task RecordRefusedRecipientsAsync(ClaimedMessage claim, IReadOnlyList<RefusedRecipient> refused, CancellationToken cancellationToken = default);
}

// ExpiresAt: the caller's deadline, after which the message is worthless and is not sent (null: none).
public sealed record MessageSubmission(
    Guid TenantId,
    string Channel,
    string Recipient,
    string Payload,
    string? IdempotencyKey = null,
    DateTimeOffset? ExpiresAt = null,
    bool Simulated = false,
    int? PerDay = null,
    string? Destination = null,
    int? DestinationPerHour = null,
    TimeSpan? DestinationCooldown = null)
{
    // The widest destination the limit's column holds; a longer one is not counted.
    public const int MaxDestinationLength = 512;

    // The key a destination is stored and counted by: itself, or for one too long for the column a SHA-256 of
    // it — a long destination is limited like any other, not skipped.
    public static string DestinationKey(string destination) =>
        destination.Length <= MaxDestinationLength
            ? destination
            : "sha256:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(destination))).ToLowerInvariant();
}

public enum EnqueueOutcome
{
    Accepted,

    // The idempotency key was already used for this same request; Record is the original message.
    Replayed,

    // The idempotency key was already used for a different request.
    KeyReused,

    // No stored credential lets this tenant send on the channel — neither its own nor a shared one.
    // Nothing was stored; Record is null.
    ChannelNotConfigured,

    // The tenant already had PerDay messages accepted on the channel in the last 24 hours. Nothing was
    // stored; RetryAfter is when the oldest of them leaves the window.
    DailyLimitReached,

    // The tenant sent to this destination too recently or too often (its cooldown, or its per-hour
    // limit). Nothing was stored; RetryAfter is when the next message to it may be accepted.
    DestinationLimited
}

public sealed record EnqueueResult(EnqueueOutcome Outcome, MessageRecord? Record, TimeSpan? RetryAfter = null);

// A channel that is not getting messages through. Unsendable: unfinished messages whose tenant no
// stored credential serves on the channel (one deleted after they were accepted). FailingFor: how long
// the channel's oldest message accepted since its last successful send has been failing and waiting
// for a retry — nothing got through in that time; null when every such message is still on its first
// attempt or there is none.
public sealed record ChannelDeliveryState(string Channel, int Unsendable, TimeSpan? FailingFor);

// The content stays encrypted until the attempt reads it (IMessageOutbox.ReadPayload).
public sealed record ClaimedMessage(
    Guid Id, Guid TenantId, string Channel, string ProtectedPayload, int AttemptCount, DateTimeOffset? ExpiresAt = null, bool Simulated = false)
    : ClaimedDelivery(Id, TenantId, AttemptCount);
