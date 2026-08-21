namespace Sendway.Core;

public sealed class PushMessage
{
    public Guid TenantId { get; }
    public string DeviceToken { get; }
    public string Title { get; }
    public string Body { get; }

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
