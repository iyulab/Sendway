namespace Sendway.Core;

public sealed class SmsMessage
{
    public Guid TenantId { get; }
    public PhoneNumber To { get; }
    public string Body { get; }

    // `to` is checked here (E.164), so a malformed number is refused before anything is stored.
    // Whether the number can be reached, and how long the body may be, depends on the provider —
    // ISmsSender.Check answers that.
    public SmsMessage(Guid tenantId, string to, string body)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(body);

        TenantId = tenantId;
        To = PhoneNumber.Parse(to);
        Body = body;
    }
}
