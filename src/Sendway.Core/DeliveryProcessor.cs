using Microsoft.Extensions.Logging;

namespace Sendway.Core;

// What the delivery worker needs from a queue, whatever the queue carries.
public interface IDeliveryProcessor
{
    string Name { get; }

    int Concurrency { get; }

    DeliverySignal Signal { get; }

    // Claims the next due item and returns the attempt to run for it, or null when nothing is due.
    // Claiming and running are separate so the worker can wake another loop in between.
    Task<Func<CancellationToken, Task>?> ClaimAttemptAsync(CancellationToken cancellationToken = default);
}

// One delivery attempt at a time: claim a due item, send it, record the outcome. The attempt policy
// (timeout, retry schedule, giving up, shutdown) lives here once; a queue supplies only how its
// items are sent. Senders make a single attempt and throw.
public abstract class DeliveryProcessor<TClaim> : IDeliveryProcessor where TClaim : ClaimedDelivery
{
    // Slack between the attempt timeout and the lease expiry, covering the time it takes to record
    // the outcome. Without it an item could become claimable again while its timed-out attempt is
    // still being written down.
    private static readonly TimeSpan LeaseMargin = TimeSpan.FromSeconds(30);

    private readonly IDeliveryQueue<TClaim> _queue;
    private readonly ILogger _logger;

    protected DeliveryProcessor(IDeliveryQueue<TClaim> queue, DeliverySignal signal, ILogger logger)
    {
        _queue = queue;
        _logger = logger;
        Signal = signal;
    }

    public abstract string Name { get; }

    public DeliverySignal Signal { get; }

    public int Concurrency => Options.Concurrency;

    protected abstract DeliveryOptions Options { get; }

    protected abstract Task SendAsync(TClaim item, CancellationToken cancellationToken);

    // True when the failure means the destination itself refused the item, so another attempt would
    // fail the same way.
    protected virtual bool IsRejection(Exception exception) => false;

    // Claims and delivers one item. Returns false when nothing was due.
    public async Task<bool> ProcessNextAsync(CancellationToken stoppingToken = default)
    {
        var item = await ClaimAsync(stoppingToken);
        if (item is null)
        {
            return false;
        }

        await DeliverAsync(item, stoppingToken);
        return true;
    }

    public Task<TClaim?> ClaimAsync(CancellationToken cancellationToken = default) =>
        _queue.ClaimNextAsync(Options.AttemptTimeout + LeaseMargin, cancellationToken);

    async Task<Func<CancellationToken, Task>?> IDeliveryProcessor.ClaimAttemptAsync(CancellationToken cancellationToken)
    {
        var item = await ClaimAsync(cancellationToken);
        return item is null ? null : stoppingToken => DeliverAsync(item, stoppingToken);
    }

    // Makes the claimed attempt and records its outcome. `stoppingToken` signals shutdown: an
    // attempt it interrupts is handed back to the queue without counting.
    public async Task DeliverAsync(TClaim item, CancellationToken stoppingToken = default)
    {
        var options = Options;

        // Only reachable when earlier attempts never reported back (the worker died each time, so
        // nothing counted the failure): stop here rather than attempt without limit.
        if (item.AttemptCount > options.MaxAttempts)
        {
            await _queue.MarkFailedAsync(item, "Delivery attempts did not complete.", CancellationToken.None);
            return;
        }

        using var attemptCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        attemptCancellation.CancelAfter(options.AttemptTimeout);

        // Outcomes are recorded without a cancellation token: once the other side has answered, the
        // result must be written even if shutdown begins in the meantime.
        try
        {
            await SendAsync(item, attemptCancellation.Token);
            await _queue.MarkSentAsync(item, CancellationToken.None);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            await _queue.ReleaseAsync(item, CancellationToken.None);
        }
        catch (Exception ex) when (IsRejection(ex))
        {
            await _queue.MarkFailedAsync(item, ex.Message, CancellationToken.None);
        }
        catch (Exception ex)
        {
            var error = ex is OperationCanceledException
                ? $"No response within {options.AttemptTimeout.TotalSeconds:0} seconds."
                : ex.Message;

            // An attempt that will be retried is a warning; only giving up on the item is an error.
            var isLastAttempt = item.AttemptCount >= options.MaxAttempts;

            _logger.Log(
                isLastAttempt ? LogLevel.Error : LogLevel.Warning,
                ex,
                "{Queue} delivery attempt {Attempt} of {MaxAttempts} failed for {ItemId} (tenant {TenantId})",
                Name, item.AttemptCount, options.MaxAttempts, item.Id, item.TenantId);

            if (isLastAttempt)
            {
                await _queue.MarkFailedAsync(item, error, CancellationToken.None);
            }
            else
            {
                await _queue.ScheduleRetryAsync(item, error, RetryDelay(options, item.AttemptCount), CancellationToken.None);
            }
        }
    }

    private static TimeSpan RetryDelay(DeliveryOptions options, int failedAttempt) =>
        options.RetryBaseDelay * Math.Pow(options.RetryBackoffFactor, failedAttempt - 1);
}
