using Microsoft.Extensions.Options;
using Sendway.Core;

namespace Sendway.Service.Delivery;

// Hosts the delivery loops of every queue (messages, webhook events). Registered after
// ChannelCredentialSeeder so the schema is migrated before the first claim runs (hosted services
// start in registration order).
public sealed class DeliveryWorker : BackgroundService
{
    private readonly IReadOnlyList<IDeliveryProcessor> _processors;
    private readonly IOptions<MessageDeliveryOptions> _options;
    private readonly ILogger<DeliveryWorker> _logger;

    public DeliveryWorker(
        IEnumerable<IDeliveryProcessor> processors,
        IOptions<MessageDeliveryOptions> options,
        ILogger<DeliveryWorker> logger)
    {
        _processors = processors.ToList();
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

        // Per queue, one loop polls on a timer — that is what finds scheduled retries and expired
        // leases. The rest sleep until signalled, so an idle service costs one lookup per queue per
        // interval rather than one per loop.
        var loops =
            from processor in _processors
            from index in Enumerable.Range(0, Math.Max(1, processor.Concurrency))
            let idleWait = index == 0 ? options.PollInterval : Timeout.InfiniteTimeSpan
            select Task.Run(() => RunLoopAsync(processor, idleWait, options.PollInterval, stoppingToken), stoppingToken);

        return Task.WhenAll(loops);
    }

    private async Task RunLoopAsync(IDeliveryProcessor processor, TimeSpan idleWait, TimeSpan errorBackoff, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var attempt = await processor.ClaimAttemptAsync(stoppingToken);
                if (attempt is null)
                {
                    await processor.Signal.WaitAsync(idleWait, stoppingToken);
                    continue;
                }

                // Where there is one due item there may be more: wake another loop to look while
                // this one is busy with the outbound call.
                processor.Signal.Notify();

                await attempt(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Failures of a delivery attempt are recorded on the item by the processor; what
                // reaches here is the loop itself failing (typically the database being unreachable).
                // Back off and try again rather than letting delivery stop for good.
                _logger.LogError(ex, "The {Queue} delivery loop failed; retrying in {Backoff}", processor.Name, errorBackoff);

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
