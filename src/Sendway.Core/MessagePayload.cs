using System.Text.Json;

namespace Sendway.Core;

// The queued form of a message: what the delivery worker needs to rebuild the channel message later.
// Property order is fixed by the record declarations, so the same request always serializes to the
// same text — the idempotency fingerprint relies on that.
public static class MessagePayload
{
    public static string Serialize(EmailMessage message) =>
        JsonSerializer.Serialize(new EmailPayload(message.To, message.Cc, message.Bcc, message.Subject, message.Body, message.HtmlBody));

    public static string Serialize(PushMessage message) =>
        JsonSerializer.Serialize(new PushPayload(message.DeviceToken, message.Title, message.Body));

    public static string Serialize(SmsMessage message) =>
        JsonSerializer.Serialize(new SmsPayload(message.To.ToString(), message.Body));

    public static EmailMessage ToEmailMessage(Guid tenantId, string payload)
    {
        var email = JsonSerializer.Deserialize<EmailPayload>(payload)
            ?? throw new InvalidOperationException("The queued email payload is empty.");

        return new EmailMessage(tenantId, email.To, email.Subject, email.Body, email.Cc, email.Bcc, email.HtmlBody);
    }

    public static PushMessage ToPushMessage(Guid tenantId, string payload)
    {
        var push = JsonSerializer.Deserialize<PushPayload>(payload)
            ?? throw new InvalidOperationException("The queued push payload is empty.");

        return new PushMessage(tenantId, push.DeviceToken, push.Title, push.Body);
    }

    public static SmsMessage ToSmsMessage(Guid tenantId, string payload)
    {
        var sms = JsonSerializer.Deserialize<SmsPayload>(payload)
            ?? throw new InvalidOperationException("The queued SMS payload is empty.");

        return new SmsMessage(tenantId, sms.To, sms.Body);
    }

    private sealed record EmailPayload(
        IReadOnlyList<string> To,
        IReadOnlyList<string> Cc,
        IReadOnlyList<string> Bcc,
        string Subject,
        string Body,
        string? HtmlBody);

    private sealed record PushPayload(string DeviceToken, string Title, string Body);

    private sealed record SmsPayload(string To, string Body);
}
