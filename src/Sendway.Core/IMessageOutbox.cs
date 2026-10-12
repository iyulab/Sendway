namespace Sendway.Core;

public interface IMessageOutbox : IDeliveryQueue<ClaimedMessage>
{
    // Durably accepts a message for delivery. When an idempotency key is given, a second call with
    // the same tenant and key never creates a second message — including while the first is still
    // being delivered.
    Task<EnqueueResult> EnqueueAsync(MessageSubmission submission, CancellationToken cancellationToken = default);

    Task<MessageRecord?> GetAsync(Guid id, CancellationToken cancellationToken = default);
}

public sealed record MessageSubmission(Guid TenantId, string Channel, string Recipient, string Payload, string? IdempotencyKey = null);

public enum EnqueueOutcome
{
    Accepted,

    // The idempotency key was already used for this same request; Record is the original message.
    Replayed,

    // The idempotency key was already used for a different request.
    KeyReused
}

public sealed record EnqueueResult(EnqueueOutcome Outcome, MessageRecord Record);

public sealed record ClaimedMessage(Guid Id, Guid TenantId, string Channel, string Payload, int AttemptCount)
    : ClaimedDelivery(Id, TenantId, AttemptCount);
