namespace Sendway.Core;

public sealed class EmailMessage
{
    public string To { get; }
    public string Subject { get; }
    public string Body { get; }

    public EmailMessage(string to, string subject, string body)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(to);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(body);

        To = to;
        Subject = subject;
        Body = body;
    }
}
