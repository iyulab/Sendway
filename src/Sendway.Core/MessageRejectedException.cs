namespace Sendway.Core;

// The provider refused the message itself (its content, or a recipient who opted out), so sending it
// again would be refused the same way. Unlike a failed attempt, it is not retried.
public sealed class MessageRejectedException : Exception
{
    public MessageRejectedException(string message, string reason = MessageFailureReasons.MessageRefused) : base(message)
    {
        Reason = reason;
    }

    public MessageRejectedException(string message, Exception innerException, string reason = MessageFailureReasons.MessageRefused)
        : base(message, innerException)
    {
        Reason = reason;
    }

    // One of MessageFailureReasons.
    public string Reason { get; }
}
