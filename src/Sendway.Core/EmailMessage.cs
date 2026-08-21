namespace Sendway.Core;

public sealed class EmailMessage
{
    public Guid TenantId { get; }
    public string To { get; }
    public string Subject { get; }
    public string Body { get; }

    public EmailMessage(Guid tenantId, string to, string subject, string body)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(to);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(body);

        TenantId = tenantId;
        To = to;
        Subject = subject;
        Body = body;
    }
}
