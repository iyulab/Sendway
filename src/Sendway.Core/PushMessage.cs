namespace Sendway.Core;

public sealed class PushMessage
{
    public Guid TenantId { get; }
    public string DeviceToken { get; }
    public string Title { get; }
    public string Body { get; }

    // Past this the device should not be shown the notification (FCM/APNs drop it if still undelivered).
    public DateTimeOffset? ExpiresAt { get; init; }

    public PushMessage(Guid tenantId, string deviceToken, string title, string body)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(body);

        TenantId = tenantId;
        DeviceToken = deviceToken;
        Title = title;
        Body = body;
    }
}
