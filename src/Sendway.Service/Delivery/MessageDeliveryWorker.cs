using Microsoft.Extensions.Options;
using Sendway.Core;

namespace Sendway.Service.Delivery;

// Hosts the delivery loops. Registered after ChannelCredentialSeeder so the schema is migrated
// before the first claim runs (hosted services start in registration order).
public sealed class MessageDeliveryWorker : BackgroundService
{
    private readonly MessageDeliveryProcessor _processor;
    private readonly DeliverySignal _signal;
    private readonly IOptions<MessageDeliveryOptions> _options;
    private readonly ILogger<MessageDeliveryWorker> _logger;

    public MessageDeliveryWorker(
        MessageDeliveryProcessor processor,
        DeliverySignal signal,
        IOptions<MessageDeliveryOptions> options,
        ILogger<MessageDeliveryWorker> logger)
    {
        _processor = processor;
        _signal = signal;
        _options = options;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = _options.Value;
        if (!options.WorkerEnabled)
        {
            return Task.CompletedTask;
        }

        // One loop polls on a timer — that is what finds scheduled retries and expired leases. The
        // rest sleep until signalled, so an idle service costs one lookup per interval rather than
        // one per loop.
        var loops = Enumerable.Range(0, Math.Max(1, options.Concurrency))
            .Select(index => index == 0 ? options.PollInterval : Timeout.InfiniteTimeSpan)
            .Select(idleWait => Task.Run(() => RunLoopAsync(idleWait, options.PollInterval, stoppingToken), stoppingToken));

        return Task.WhenAll(loops);
    }

    private async Task RunLoopAsync(TimeSpan idleWait, TimeSpan errorBackoff, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var message = await _processor.ClaimAsync(stoppingToken);
                if (message is null)
                {
                    await _signal.WaitAsync(idleWait, stoppingToken);
                    continue;
                }

                // Where there is one due message there may be more: wake another loop to look while
                // this one is busy with the provider call.
                _signal.Notify();

                await _processor.DeliverAsync(message, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Failures of a delivery attempt are recorded on the message by the processor; what
                // reaches here is the loop itself failing (typically the database being unreachable).
                // Back off and try again rather than letting delivery stop for good.
                _logger.LogError(ex, "The delivery loop failed; retrying in {Backoff}", errorBackoff);

                try
                {
                    await Task.Delay(errorBackoff, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }
}
