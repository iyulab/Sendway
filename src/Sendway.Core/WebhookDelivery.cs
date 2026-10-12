namespace Sendway.Core;

// One event waiting to be (or already) delivered to a tenant's webhook endpoint. Its Id is the
// event's identity on the wire: it stays the same across delivery attempts, which is what lets a
// receiver recognise a redelivery. Payload is the exact request body.
public sealed class WebhookDelivery : QueuedDelivery
{
    public required Guid MessageId { get; init; }

    public required string EventType { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
}
