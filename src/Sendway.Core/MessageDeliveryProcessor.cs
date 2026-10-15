using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Sendway.Core;

// Delivers accepted messages through their channel's sender.
public sealed class MessageDeliveryProcessor : DeliveryProcessor<ClaimedMessage>
{
    private readonly IMessageOutbox _outbox;
    private readonly IEmailSender _emailSender;
    private readonly IPushSender _pushSender;
    private readonly ISmsSender _smsSender;
    private readonly IOptions<MessageDeliveryOptions> _options;
    private readonly TimeProvider _timeProvider;

    public MessageDeliveryProcessor(
        IMessageOutbox outbox,
        IEmailSender emailSender,
        IPushSender pushSender,
        ISmsSender smsSender,
        IOptions<MessageDeliveryOptions> options,
        MessageDeliverySignal signal,
        ILogger<MessageDeliveryProcessor> logger,
        TimeProvider? timeProvider = null)
        : base(outbox, signal, logger)
    {
        _outbox = outbox;
        _emailSender = emailSender;
        _pushSender = pushSender;
        _smsSender = smsSender;
        _options = options;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public override string Name => "Message";

    protected override DeliveryOptions Options => _options.Value;

    protected override async Task SendAsync(ClaimedMessage message, CancellationToken cancellationToken)
    {
        if (message.ExpiresAt is { } expiresAt && expiresAt <= _timeProvider.GetUtcNow())
        {
            throw new MessageRejectedException(
                $"The message expired at {expiresAt:O} before it could be sent.", MessageFailureReasons.Expired);
        }

        var payload = _outbox.ReadPayload(message);

        switch (message.Channel)
        {
            case MessageChannels.Email:
                var delivery = await _emailSender.SendAsync(MessagePayload.ToEmailMessage(message.TenantId, payload), cancellationToken);
                if (delivery.RefusedRecipients.Count > 0)
                {
                    // Not cancellable: the message has gone out, what it left out must be recorded.
                    await _outbox.RecordRefusedRecipientsAsync(message, delivery.RefusedRecipients, CancellationToken.None);
                }

                break;
            case MessageChannels.Push:
                await _pushSender.SendAsync(MessagePayload.ToPushMessage(message.TenantId, payload, message.ExpiresAt), cancellationToken);
                break;
            case MessageChannels.Sms:
                await _smsSender.SendAsync(MessagePayload.ToSmsMessage(message.TenantId, payload), cancellationToken);
                break;
            default:
                throw new InvalidOperationException($"No sender is registered for channel '{message.Channel}'.");
        }
    }

    protected override bool IsRejection(Exception exception) => exception is InvalidRecipientException or MessageRejectedException;

    protected override string FailureReason(Exception? rejection) => rejection switch
    {
        InvalidRecipientException => MessageFailureReasons.RecipientRefused,
        MessageRejectedException rejected => rejected.Reason,
        _ => MessageFailureReasons.AttemptsExhausted
    };
}
