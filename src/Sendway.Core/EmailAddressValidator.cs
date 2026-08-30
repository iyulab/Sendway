using MimeKit;

namespace Sendway.Core;

// Shared by SmtpEmailSender and GraphEmailSender so a malformed address is rejected identically
// (InvalidRecipientException, before any network call) regardless of which channel a tenant is
// configured for.
internal static class EmailAddressValidator
{
    public static MailboxAddress Validate(string address)
    {
        // MailboxAddress.TryParse alone accepts a bare local-part with no "@" (e.g. "plainaddress")
        // as a valid mailbox — too lenient for rejecting a malformed address, so the "@" is checked
        // explicitly on top of it.
        if (!MailboxAddress.TryParse(address, out var mailbox) || !mailbox.Address.Contains('@'))
        {
            throw new InvalidRecipientException($"'{address}' is not a valid email address.");
        }

        return mailbox;
    }
}
