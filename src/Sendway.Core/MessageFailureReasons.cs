namespace Sendway.Core;

// Why a message ended Failed, in words a caller's code can branch on. Each value says who can act on
// it: the caller (fix or drop the destination, change the content, wait), or whoever runs the
// deployment (a failure that kept happening until the attempts ran out). The human-readable `Error`
// stays alongside for the detail.
public static class MessageFailureReasons
{
    // The destination refused or does not exist: an unknown mailbox, an invalid or unregistered
    // device token, a recipient who opted out of messages from this sender.
    public const string RecipientRefused = "recipient_refused";

    // The provider refused the message itself — its content, size or form — so the same message
    // would be refused again.
    public const string MessageRefused = "message_refused";

    // The provider refused because the destination already got as many messages as it allows in a
    // short window. Sending later would deliver it late rather than not at all.
    public const string RecipientRateLimited = "recipient_rate_limited";

    // Every attempt failed for a reason that could have cleared up (the provider unreachable or
    // overloaded, a credential or sender identity it refused) and there are none left.
    public const string AttemptsExhausted = "attempts_exhausted";
}
