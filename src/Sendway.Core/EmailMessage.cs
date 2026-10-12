namespace Sendway.Core;

public sealed class EmailMessage
{
    public Guid TenantId { get; }
    public IReadOnlyList<string> To { get; }
    public IReadOnlyList<string> Cc { get; }
    public IReadOnlyList<string> Bcc { get; }
    public string Subject { get; }
    public string Body { get; }
    public string? HtmlBody { get; }

    public EmailMessage(
        Guid tenantId,
        IReadOnlyList<string> to,
        string subject,
        string body,
        IReadOnlyList<string>? cc = null,
        IReadOnlyList<string>? bcc = null,
        string? htmlBody = null)
    {
        if (to is null || to.Count == 0)
        {
            throw new ArgumentException("At least one 'to' recipient is required.", nameof(to));
        }

        foreach (var address in to)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(address, nameof(to));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(body);

        cc ??= [];
        bcc ??= [];

        // A message is accepted for delivery before any sender sees it, so a malformed address has
        // to be rejected here (InvalidRecipientException) for the caller to hear about it at all.
        foreach (var address in to.Concat(cc).Concat(bcc))
        {
            EmailAddressValidator.Validate(address);
        }

        TenantId = tenantId;
        To = to;
        Cc = cc;
        Bcc = bcc;
        Subject = subject;
        Body = body;
        HtmlBody = htmlBody;
    }
}
