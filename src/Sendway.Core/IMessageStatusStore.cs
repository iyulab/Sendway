namespace Sendway.Core;

public interface IMessageStatusStore
{
    Task<Guid> RecordAsync(
        Guid tenantId,
        string channel,
        string recipient,
        MessageDeliveryStatus status,
        string? error,
        CancellationToken cancellationToken = default);

    Task<MessageRecord?> GetAsync(Guid id, CancellationToken cancellationToken = default);
}
