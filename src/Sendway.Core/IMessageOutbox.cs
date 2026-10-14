namespace Sendway.Core;

public interface IMessageOutbox : IDeliveryQueue<ClaimedMessage>
{
    // Durably accepts a message for delivery. When an idempotency key is given, a second call with
    // the same tenant and key never creates a second message — including while the first is still
    // being delivered.
    Task<EnqueueResult> EnqueueAsync(MessageSubmission submission, CancellationToken cancellationToken = default);

    Task<MessageRecord?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    // Messages not yet sent or failed on a channel that no stored credential lets their tenant send
    // on (one deleted after they were accepted), counted per channel. Empty when there are none.
    Task<IReadOnlyDictionary<string, int>> CountUnsendableAsync(CancellationToken cancellationToken = default);

    // The claimed message's content, decrypted. Throws when it cannot be — part of the attempt, so
    // the failure is retried and counted like any other rather than stopping the claim.
    string ReadPayload(ClaimedMessage message);
}

public sealed record MessageSubmission(Guid TenantId, string Channel, string Recipient, string Payload, string? IdempotencyKey = null);

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

// The content stays encrypted until the attempt reads it (IMessageOutbox.ReadPayload).
public sealed record ClaimedMessage(Guid Id, Guid TenantId, string Channel, string ProtectedPayload, int AttemptCount)
    : ClaimedDelivery(Id, TenantId, AttemptCount);
