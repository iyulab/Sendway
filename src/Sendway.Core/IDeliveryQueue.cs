namespace Sendway.Core;

// One claimed attempt at delivering a queued item.
public abstract record ClaimedDelivery(Guid Id, Guid TenantId, int AttemptCount);

public interface IDeliveryQueue<TClaim> where TClaim : ClaimedDelivery
{
    // Claims the next due item for one delivery attempt, or returns null when nothing is due. The
    // claim is a lease: if it is neither completed nor released within `lease`, the item becomes
    // claimable again.
    Task<TClaim?> ClaimNextAsync(TimeSpan lease, CancellationToken cancellationToken = default);

    Task MarkSentAsync(TClaim claim, CancellationToken cancellationToken = default);

    Task MarkFailedAsync(TClaim claim, string error, CancellationToken cancellationToken = default);

    Task ScheduleRetryAsync(TClaim claim, string error, TimeSpan delay, CancellationToken cancellationToken = default);

    // Hands a claimed item back untouched (the attempt doesn't count) — for a worker that is
    // shutting down before the attempt could finish.
    Task ReleaseAsync(TClaim claim, CancellationToken cancellationToken = default);

    Task<QueueStats> GetStatsAsync(CancellationToken cancellationToken = default);
}

// A point-in-time view of one queue, for telling a healthy queue from a stuck one. OldestDueAge is
// how long the longest-waiting claimable item has been claimable: near zero while delivery keeps up,
// growing when nothing is picking items up. Null when nothing is due.
public sealed record QueueStats(int Queued, int Sending, int FailedInLast24Hours, TimeSpan? OldestDueAge);
