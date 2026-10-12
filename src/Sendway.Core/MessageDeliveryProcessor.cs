using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Sendway.Core;

// One delivery attempt at a time: claim a due message, hand it to its channel's sender, record the
// outcome. This is the only place retry policy lives — senders make a single attempt and throw.
public sealed class MessageDeliveryProcessor
{
    // Slack between the attempt timeout and the lease expiry, covering the time it takes to record
    // the outcome. Without it a message could become claimable again while its timed-out attempt
    // is still being written down.
    private static readonly TimeSpan LeaseMargin = TimeSpan.FromSeconds(30);

    private readonly IMessageOutbox _outbox;
    private readonly IEmailSender _emailSender;
    private readonly IPushSender _pushSender;
    private readonly IOptions<MessageDeliveryOptions> _options;
    private readonly ILogger<MessageDeliveryProcessor> _logger;

    public MessageDeliveryProcessor(
        IMessageOutbox outbox,
        IEmailSender emailSender,
        IPushSender pushSender,
        IOptions<MessageDeliveryOptions> options,
        ILogger<MessageDeliveryProcessor> logger)
    {
        _outbox = outbox;
        _emailSender = emailSender;
        _pushSender = pushSender;
        _options = options;
        _logger = logger;
    }

    // Claims and delivers one message. Returns false when no message was due.
    public async Task<bool> ProcessNextAsync(CancellationToken stoppingToken = default)
    {
        var message = await ClaimAsync(stoppingToken);
        if (message is null)
        {
            return false;
        }

        await DeliverAsync(message, stoppingToken);
        return true;
    }

    public Task<ClaimedMessage?> ClaimAsync(CancellationToken cancellationToken = default) =>
        _outbox.ClaimNextAsync(_options.Value.AttemptTimeout + LeaseMargin, cancellationToken);

    // Makes the claimed attempt and records its outcome. `stoppingToken` signals shutdown: an
    // attempt it interrupts is handed back to the queue without counting.
    public async Task DeliverAsync(ClaimedMessage message, CancellationToken stoppingToken = default)
    {
        var options = _options.Value;

        // Only reachable when earlier attempts never reported back (the worker died each time, so
        // nothing counted the failure): stop here rather than attempt without limit.
        if (message.AttemptCount > options.MaxAttempts)
        {
            await _outbox.MarkFailedAsync(message, "Delivery attempts did not complete.", CancellationToken.None);
            return;
        }

        using var attemptCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        attemptCancellation.CancelAfter(options.AttemptTimeout);

        // Outcomes are recorded without a cancellation token: once the provider has answered, the
        // result must be written even if shutdown begins in the meantime.
        try
        {
            await SendAsync(message, attemptCancellation.Token);
            await _outbox.MarkSentAsync(message, CancellationToken.None);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            await _outbox.ReleaseAsync(message, CancellationToken.None);
        }
        catch (InvalidRecipientException ex)
        {
            // The destination itself was rejected — another attempt would fail the same way.
            await _outbox.MarkFailedAsync(message, ex.Message, CancellationToken.None);
        }
        catch (Exception ex)
        {
            var error = ex is OperationCanceledException
                ? $"The provider did not respond within {options.AttemptTimeout.TotalSeconds:0} seconds."
                : ex.Message;

            // An attempt that will be retried is a warning; only giving up on the message is an error.
            var isLastAttempt = message.AttemptCount >= options.MaxAttempts;

            _logger.Log(
                isLastAttempt ? LogLevel.Error : LogLevel.Warning,
                ex,
                "Delivery attempt {Attempt} of {MaxAttempts} failed for message {MessageId} (tenant {TenantId}, channel {Channel})",
                message.AttemptCount, options.MaxAttempts, message.Id, message.TenantId, message.Channel);

            if (isLastAttempt)
            {
                await _outbox.MarkFailedAsync(message, error, CancellationToken.None);
            }
            else
            {
                await _outbox.ScheduleRetryAsync(message, error, RetryDelay(options, message.AttemptCount), CancellationToken.None);
            }
        }
    }

    private Task SendAsync(ClaimedMessage message, CancellationToken cancellationToken) => message.Channel switch
    {
        MessageChannels.Email => _emailSender.SendAsync(MessagePayload.ToEmailMessage(message.TenantId, message.Payload), cancellationToken),
        MessageChannels.Push => _pushSender.SendAsync(MessagePayload.ToPushMessage(message.TenantId, message.Payload), cancellationToken),
        _ => throw new InvalidOperationException($"No sender is registered for channel '{message.Channel}'.")
    };

    private static TimeSpan RetryDelay(MessageDeliveryOptions options, int failedAttempt) =>
        options.RetryBaseDelay * Math.Pow(options.RetryBackoffFactor, failedAttempt - 1);
}
