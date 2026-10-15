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
    DateTimeOffset? ExpiresAt = null);

public enum EnqueueOutcome
{
    Accepted,

    // The idempotency key was already used for this same request; Record is the original message.
    Replayed,

    // The idempotency key was already used for a different request.
    KeyReused,

    // No stored credential lets this tenant send on the channel — neither its own nor a shared one.
    // Nothing was stored; Record is null.
    ChannelNotConfigured
}

public sealed record EnqueueResult(EnqueueOutcome Outcome, MessageRecord? Record);

// A channel that is not getting messages through. Unsendable: unfinished messages whose tenant no
// stored credential serves on the channel (one deleted after they were accepted). FailingFor: how long
// the channel's oldest message accepted since its last successful send has been failing and waiting
// for a retry — nothing got through in that time; null when every such message is still on its first
// attempt or there is none.
public sealed record ChannelDeliveryState(string Channel, int Unsendable, TimeSpan? FailingFor);

// The content stays encrypted until the attempt reads it (IMessageOutbox.ReadPayload).
public sealed record ClaimedMessage(Guid Id, Guid TenantId, string Channel, string ProtectedPayload, int AttemptCount, DateTimeOffset? ExpiresAt = null)
    : ClaimedDelivery(Id, TenantId, AttemptCount);
