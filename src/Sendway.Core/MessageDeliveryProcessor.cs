using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Sendway.Core;

// Delivers accepted messages through their channel's sender.
public sealed class MessageDeliveryProcessor : DeliveryProcessor<ClaimedMessage>
{
    private readonly IEmailSender _emailSender;
    private readonly IPushSender _pushSender;
    private readonly IOptions<MessageDeliveryOptions> _options;

    public MessageDeliveryProcessor(
        IMessageOutbox outbox,
        IEmailSender emailSender,
        IPushSender pushSender,
        IOptions<MessageDeliveryOptions> options,
        MessageDeliverySignal signal,
        ILogger<MessageDeliveryProcessor> logger)
        : base(outbox, signal, logger)
    {
        _emailSender = emailSender;
        _pushSender = pushSender;
        _options = options;
    }

    public override string Name => "Message";

    protected override DeliveryOptions Options => _options.Value;

    protected override Task SendAsync(ClaimedMessage message, CancellationToken cancellationToken) => message.Channel switch
    {
        MessageChannels.Email => _emailSender.SendAsync(MessagePayload.ToEmailMessage(message.TenantId, message.Payload), cancellationToken),
        MessageChannels.Push => _pushSender.SendAsync(MessagePayload.ToPushMessage(message.TenantId, message.Payload), cancellationToken),
        _ => throw new InvalidOperationException($"No sender is registered for channel '{message.Channel}'.")
    };

    protected override bool IsRejection(Exception exception) => exception is InvalidRecipientException;
}
