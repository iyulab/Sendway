using System.Text.Json;

namespace Sendway.Core;

// The event announcing a message's final outcome. The envelope follows the Standard Webhooks
// payload convention (type, timestamp, data); `data` carries the same fields, under the same names,
// as the message status a caller can query, so a receiver handles both with one shape.
public static class MessageEvent
{
    public const string Sent = "message.sent";
    public const string Failed = "message.failed";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static string TypeFor(DeliveryStatus status) => status == DeliveryStatus.Sent ? Sent : Failed;

    public static string Serialize(MessageRecord message, DateTimeOffset occurredAt) =>
        JsonSerializer.Serialize(
            new Envelope(
                TypeFor(message.Status),
                occurredAt,
                new Data(
                    message.Id,
                    message.Channel,
                    message.Recipient,
                    message.Status.ToString(),
                    message.Error,
                    message.AttemptCount,
                    message.AcceptedAt,
                    message.CompletedAt)),
            SerializerOptions);

    private sealed record Envelope(string Type, DateTimeOffset Timestamp, Data Data);

    private sealed record Data(
        Guid Id,
        string Channel,
        string Recipient,
        string Status,
        string? Error,
        int Attempts,
        DateTimeOffset AcceptedAt,
        DateTimeOffset? CompletedAt);
}
