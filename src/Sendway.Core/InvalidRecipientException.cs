namespace Sendway.Core;

public sealed class InvalidRecipientException : Exception
{
    public InvalidRecipientException(string message) : base(message)
    {
    }

    public InvalidRecipientException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
