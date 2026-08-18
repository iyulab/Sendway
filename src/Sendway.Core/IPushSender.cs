namespace Sendway.Core;

public interface IPushSender
{
    Task SendAsync(PushMessage message, CancellationToken cancellationToken = default);
}
