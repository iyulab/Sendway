using System.Text.RegularExpressions;
using MailKit.Net.Smtp;

namespace Sendway.Core;

// Tells a permanent refusal by the SMTP server from a temporary one. A reply in the 5yz class (RFC 5321
// §4.2.1) means the same transaction will be refused again, so it ends the message instead of being
// retried; 4yz asks to try later. The enhanced status code (RFC 3463, "5.1.1") is read first when the
// reply carries one — it is the part servers keep precise, and it settles the rare reply whose basic
// code says otherwise.
internal static partial class SmtpRefusals
{
    // The exception that ends delivery for a refusal that will not change on retry, or null when the
    // refusal is temporary or one an operator can fix (a refused sender: an unverified address, a
    // policy) — those stay retried, like the other channels' refusals an operator can correct.
    public static Exception? AsFinal(SmtpCommandException exception)
    {
        if (!IsPermanent(exception))
        {
            return null;
        }

        return exception.ErrorCode switch
        {
            SmtpErrorCode.RecipientNotAccepted =>
                new InvalidRecipientException($"The mail server refused the recipient: {exception.Message}", exception),
            SmtpErrorCode.MessageNotAccepted =>
                new MessageRejectedException($"The mail server refused the message: {exception.Message}", exception),
            _ => null
        };
    }

    private static bool IsPermanent(SmtpCommandException exception)
    {
        var enhanced = EnhancedStatusCode().Match(exception.Message);
        return enhanced.Success
            ? enhanced.Groups[1].Value == "5"
            : (int)exception.StatusCode is >= 500 and < 600;
    }

    [GeneratedRegex(@"^\s*([245])\.\d{1,3}\.\d{1,3}\b")]
    private static partial Regex EnhancedStatusCode();
}
