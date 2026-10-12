using Microsoft.EntityFrameworkCore;

namespace Sendway.Core;

public sealed record ClaimedWebhook(Guid Id, Guid TenantId, string Payload, int AttemptCount)
    : ClaimedDelivery(Id, TenantId, AttemptCount);

// The queue of events waiting to reach tenants' webhook endpoints. Events are put here by
// MessageOutbox, in the same transaction that records the outcome they announce.
public sealed class WebhookOutbox : IDeliveryQueue<ClaimedWebhook>
{
    private readonly IDbContextFactory<SendwayDbContext> _dbContextFactory;
    private readonly DeliveryQueue<WebhookDelivery> _queue;

    public WebhookOutbox(IDbContextFactory<SendwayDbContext> dbContextFactory, TimeProvider timeProvider)
    {
        _dbContextFactory = dbContextFactory;
        _queue = new DeliveryQueue<WebhookDelivery>(timeProvider);
    }

    public async Task<ClaimedWebhook?> ClaimNextAsync(TimeSpan lease, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var delivery = await _queue.ClaimNextAsync(db, lease, cancellationToken);

        return delivery is null
            ? null
            : new ClaimedWebhook(delivery.Id, delivery.TenantId, delivery.Payload!, delivery.AttemptCount);
    }

    public async Task MarkSentAsync(ClaimedWebhook claim, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        await _queue.CompleteAsync(db, claim, DeliveryStatus.Sent, error: null, cancellationToken);
    }

    public async Task MarkFailedAsync(ClaimedWebhook claim, string error, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        await _queue.CompleteAsync(db, claim, DeliveryStatus.Failed, error, cancellationToken);
    }

    public async Task ScheduleRetryAsync(ClaimedWebhook claim, string error, TimeSpan delay, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        await _queue.ScheduleRetryAsync(db, claim, error, delay, cancellationToken);
    }

    public async Task ReleaseAsync(ClaimedWebhook claim, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        await _queue.ReleaseAsync(db, claim, cancellationToken);
    }
}
