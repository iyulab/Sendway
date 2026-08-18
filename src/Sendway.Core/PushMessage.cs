namespace Sendway.Core;

public sealed class PushMessage
{
    public string DeviceToken { get; }
    public string Title { get; }
    public string Body { get; }

    public PushMessage(string deviceToken, string title, string body)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(body);

        DeviceToken = deviceToken;
        Title = title;
        Body = body;
    }
}
