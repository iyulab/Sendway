using MailKit.Net.Smtp;
using MimeKit;

namespace Sendway.Core;

// An SMTP client that sends to the recipients a server accepts when it refuses others for good, and
// remembers which it refused. By default MailKit abandons the whole message at the first refused
// recipient — one mistyped address would keep every other recipient from getting it. A temporary
// refusal still abandons the message, before its content is sent: nobody has received it, so retrying
// it whole sends no one a second copy. One message at a time (the connection pool hands a connection
// to one sender), so the refusals belong to the message being sent.
internal sealed class TrackingSmtpClient : SmtpClient
{
    private readonly List<RefusedRecipient> _refused = [];

    // The refusals of the message just sent, cleared for the next one.
    public IReadOnlyList<RefusedRecipient> TakeRefused()
    {
        var refused = _refused.ToList();
        _refused.Clear();
        return refused;
    }

    public void ForgetRefused() => _refused.Clear();

    protected override void OnRecipientNotAccepted(MimeMessage message, MailboxAddress mailbox, SmtpResponse response)
    {
        if (!SmtpRefusals.IsPermanent(response.StatusCode, response.Response))
        {
            base.OnRecipientNotAccepted(message, mailbox, response);
        }

        _refused.Add(new RefusedRecipient(mailbox.Address, $"{(int)response.StatusCode} {response.Response}"));
    }

    protected override void OnNoRecipientsAccepted(MimeMessage message)
    {
        var refused = string.Join("; ", _refused.Select(r => $"{r.Address}: {r.Error}"));
        _refused.Clear();
        throw new InvalidRecipientException($"The mail server refused the recipient: {refused}");
    }
}
