using MimeKit;

namespace Sendway.Core;

// Shared by SmtpEmailSender and GraphEmailSender so a malformed address is rejected identically
// (InvalidRecipientException, before any network call) regardless of which channel a tenant is
// configured for.
// The key a per-destination limit counts an email address by: the mailbox the address names, not how it was
// written — "Name <a@x.com>", "A@X.COM" and an internationalized domain in Unicode or in punycode are one
// destination. Provider-specific aliases (a "+tag", dots in a Gmail address) are not folded: that is the
// provider's rule, not ours to guess.
public static class EmailDestination
{
    public static string Key(string address) =>
        EmailAddressValidator.Validate(address).GetAddress(true).ToLowerInvariant();
}

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
