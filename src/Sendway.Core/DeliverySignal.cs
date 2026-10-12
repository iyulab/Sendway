namespace Sendway.Core;

// Wakes an idle delivery loop when there is (or may be) an item to pick up, so delivery doesn't
// wait for the next poll. Purely an in-process latency shortcut: a missed or surplus signal costs
// at most one poll interval or one empty lookup, never an item.
public class DeliverySignal
{
    // More pending wake-ups than this adds nothing: a loop that wakes keeps claiming until the
    // queue has nothing due.
    private const int MaxPending = 64;

    private readonly SemaphoreSlim _pending = new(0, MaxPending);

    public void Notify()
    {
        try
        {
            _pending.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }

    // Completes when signalled, or after `timeout` (Timeout.InfiniteTimeSpan to wait for a signal only).
    public async Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        await _pending.WaitAsync(timeout, cancellationToken);
    }
}

// One signal per queue, as distinct types so each can be injected where it belongs.
public sealed class MessageDeliverySignal : DeliverySignal;

public sealed class WebhookDeliverySignal : DeliverySignal;
