using Microsoft.EntityFrameworkCore;

namespace Sendway.Core;

// The lease mechanics shared by every queue of outbound work: claim the next due item for one
// attempt, then record that attempt's outcome — safely when several workers, or several instances,
// are doing the same thing against one database.
internal sealed class DeliveryQueue<TEntity> where TEntity : QueuedDelivery
{
    private readonly TimeProvider _timeProvider;

    public DeliveryQueue(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    public async Task<TEntity?> ClaimNextAsync(SendwayDbContext db, TimeSpan lease, CancellationToken cancellationToken)
    {
        while (true)
        {
            DateTimeOffset? now = _timeProvider.GetUtcNow();

            var candidate = await db.Set<TEntity>()
                .AsNoTracking()
                .Where(m => (m.Status == DeliveryStatus.Queued || m.Status == DeliveryStatus.Sending) && m.NextAttemptAt <= now)
                .OrderBy(m => m.NextAttemptAt)
                .Select(m => new { m.Id, m.AttemptCount })
                .FirstOrDefaultAsync(cancellationToken);

            if (candidate is null)
            {
                return null;
            }

            // Conditional on the attempt count just read: if another worker claimed this row in
            // between, its increment makes this update match nothing and the loop moves on. A plain
            // UPDATE with an affected-row check rather than SELECT ... FOR UPDATE SKIP LOCKED, so the
            // same code runs on every supported database.
            var attempt = candidate.AttemptCount + 1;
            DateTimeOffset? leaseExpiry = now + lease;

            var claimed = await db.Set<TEntity>()
                .Where(m => m.Id == candidate.Id && m.AttemptCount == candidate.AttemptCount)
                .ExecuteUpdateAsync(
                    update => update
                        .SetProperty(m => m.Status, DeliveryStatus.Sending)
                        .SetProperty(m => m.AttemptCount, attempt)
                        .SetProperty(m => m.NextAttemptAt, leaseExpiry),
                    cancellationToken);

            if (claimed == 0)
            {
                continue;
            }

            return await db.Set<TEntity>().AsNoTracking().FirstAsync(m => m.Id == candidate.Id, cancellationToken);
        }
    }

    // Returns the number of rows updated: 1 when the outcome was recorded, 0 when it was dropped
    // because the attempt no longer owns the item (see OwnedAttempt).
    public Task<int> CompleteAsync(SendwayDbContext db, ClaimedDelivery claim, DeliveryStatus status, string? error, CancellationToken cancellationToken)
    {
        DateTimeOffset? completedAt = _timeProvider.GetUtcNow();

        return OwnedAttempt(db, claim).ExecuteUpdateAsync(
            update => update
                .SetProperty(m => m.Status, status)
                .SetProperty(m => m.Error, error)
                .SetProperty(m => m.CompletedAt, completedAt)
                .SetProperty(m => m.NextAttemptAt, (DateTimeOffset?)null)
                .SetProperty(m => m.Payload, (string?)null),
            cancellationToken);
    }

    public Task<int> ScheduleRetryAsync(SendwayDbContext db, ClaimedDelivery claim, string error, TimeSpan delay, CancellationToken cancellationToken)
    {
        DateTimeOffset? nextAttemptAt = _timeProvider.GetUtcNow() + delay;

        return OwnedAttempt(db, claim).ExecuteUpdateAsync(
            update => update
                .SetProperty(m => m.Status, DeliveryStatus.Queued)
                .SetProperty(m => m.Error, error)
                .SetProperty(m => m.NextAttemptAt, nextAttemptAt),
            cancellationToken);
    }

    public Task<int> ReleaseAsync(SendwayDbContext db, ClaimedDelivery claim, CancellationToken cancellationToken)
    {
        DateTimeOffset? now = _timeProvider.GetUtcNow();
        var previousAttemptCount = claim.AttemptCount - 1;

        return OwnedAttempt(db, claim).ExecuteUpdateAsync(
            update => update
                .SetProperty(m => m.Status, DeliveryStatus.Queued)
                .SetProperty(m => m.AttemptCount, previousAttemptCount)
                .SetProperty(m => m.NextAttemptAt, now),
            cancellationToken);
    }

    // Scopes an outcome update to the attempt that was claimed. If the lease expired and another
    // worker has since claimed the item, the attempt count has moved on and the stale outcome is
    // dropped instead of overwriting the newer attempt's state.
    private static IQueryable<TEntity> OwnedAttempt(SendwayDbContext db, ClaimedDelivery claim) =>
        db.Set<TEntity>().Where(m =>
            m.Id == claim.Id &&
            m.Status == DeliveryStatus.Sending &&
            m.AttemptCount == claim.AttemptCount);
}
