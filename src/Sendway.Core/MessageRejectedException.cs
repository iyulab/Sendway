namespace Sendway.Core;

// The provider refused the message itself (its content, or a recipient who opted out), so sending it
// again would be refused the same way. Unlike a failed attempt, it is not retried.
public sealed class MessageRejectedException : Exception
{
    public MessageRejectedException(string message) : base(message)
    {
    }

    public MessageRejectedException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
