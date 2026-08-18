namespace Sendway.Core;

public sealed class MessageRecord
{
    public required Guid Id { get; init; }

    public required string Channel { get; init; }

    public required string Recipient { get; init; }

    public required MessageDeliveryStatus Status { get; init; }

    public string? Error { get; init; }

    public required DateTimeOffset SentAt { get; init; }
}
