namespace Sendway.Core;

// What a sandbox tenant's messages "receive" instead of a provider: nothing leaves the instance, and the
// destination picks the outcome so a calling service can exercise every result its code handles. The
// rest of the path is the real one — the queue, deadlines, the status and the signed webhook.
//
// Email and push: a recipient address whose local part, or a device token, starts with one of the words
// below gets that outcome. SMS: a number ending in the digits below. Anything else is sent.
//   refused     → the destination refuses it (recipient_refused; for email, listed in refusedRecipients
//                 while the other recipients receive it)
//   rejected    → the provider refuses the message itself (message_refused)
//   ratelimited → too many to that destination (recipient_rate_limited)
//   exhausted   → a failure on the sending side that outlasts every attempt (attempts_exhausted — at
//                 once, without the real retry schedule)
public static class DeliverySimulator
{
    private static readonly (string Word, string Digits, string Reason)[] Outcomes =
    [
        ("refused", "0001", MessageFailureReasons.RecipientRefused),
        ("rejected", "0002", MessageFailureReasons.MessageRefused),
        ("ratelimited", "0003", MessageFailureReasons.RecipientRateLimited),
        ("exhausted", "0004", MessageFailureReasons.AttemptsExhausted)
    ];

    public static EmailDelivery Send(EmailMessage message)
    {
        var all = message.To.Concat(message.Cc).Concat(message.Bcc).ToList();
        var refused = new List<RefusedRecipient>();

        foreach (var address in all)
        {
            switch (OutcomeFor(address[..address.IndexOf('@')], digits: null))
            {
                case MessageFailureReasons.RecipientRefused:
                    refused.Add(new RefusedRecipient(address, "550 5.1.1 Simulated: the recipient does not exist"));
                    break;
                case { } reason:
                    throw Failure(reason);
            }
        }

        if (refused.Count == all.Count)
        {
            throw new InvalidRecipientException("Simulated: every recipient was refused.");
        }

        return new EmailDelivery(refused);
    }

    public static void Send(PushMessage message)
    {
        if (OutcomeFor(message.DeviceToken, digits: null) is { } reason)
        {
            throw Failure(reason);
        }
    }

    public static void Send(SmsMessage message)
    {
        if (OutcomeFor(word: null, message.To.ToString()) is { } reason)
        {
            throw Failure(reason);
        }
    }

    private static string? OutcomeFor(string? word, string? digits) =>
        Outcomes
            .Where(o => (word is not null && word.StartsWith(o.Word, StringComparison.OrdinalIgnoreCase)) ||
                        (digits is not null && digits.EndsWith(o.Digits, StringComparison.Ordinal)))
            .Select(o => o.Reason)
            .FirstOrDefault();

    private static Exception Failure(string reason) => reason == MessageFailureReasons.RecipientRefused
        ? new InvalidRecipientException("Simulated: the destination refused the message.")
        : new MessageRejectedException($"Simulated: {reason}.", reason);
}
