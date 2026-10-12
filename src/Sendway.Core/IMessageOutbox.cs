namespace Sendway.Core;

public interface IMessageOutbox
{
    // Durably accepts a message for delivery. When an idempotency key is given, a second call with
    // the same tenant and key never creates a second message — including while the first is still
    // being delivered.
    Task<EnqueueResult> EnqueueAsync(MessageSubmission submission, CancellationToken cancellationToken = default);

    Task<MessageRecord?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    // Claims the next due message for one delivery attempt, or returns null when nothing is due.
    // The claim is a lease: if it is neither completed nor released within `lease`, the message
    // becomes claimable again.
    Task<ClaimedMessage?> ClaimNextAsync(TimeSpan lease, CancellationToken cancellationToken = default);

    Task MarkSentAsync(ClaimedMessage message, CancellationToken cancellationToken = default);

    Task MarkFailedAsync(ClaimedMessage message, string error, CancellationToken cancellationToken = default);

    Task ScheduleRetryAsync(ClaimedMessage message, string error, TimeSpan delay, CancellationToken cancellationToken = default);

    // Hands a claimed message back untouched (the attempt doesn't count) — for a worker that is
    // shutting down before the attempt could finish.
    Task ReleaseAsync(ClaimedMessage message, CancellationToken cancellationToken = default);
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

public sealed record ClaimedMessage(Guid Id, Guid TenantId, string Channel, string Payload, int AttemptCount);
