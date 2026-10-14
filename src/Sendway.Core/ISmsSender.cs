namespace Sendway.Core;

public interface ISmsSender
{
    // What would make the provider refuse this message outright (a destination it does not serve,
    // a body it cannot carry), or null when nothing would. Asked before the message is accepted, so
    // such a message is refused then rather than failing later.
    string? Check(SmsMessage message);

    Task SendAsync(SmsMessage message, CancellationToken cancellationToken = default);
}
