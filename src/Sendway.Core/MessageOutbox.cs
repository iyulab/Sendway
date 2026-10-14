using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Sendway.Core;

public sealed class MessageOutbox : IMessageOutbox
{
    private const string ProtectorPurpose = "Sendway.MessagePayload.v1";

    // How long an idempotency key stays bound to its message. Matches the common industry window
    // (e.g. Stripe's 24 hours) — long enough to cover any realistic retry schedule, bounded so a
    // caller that recycles keys isn't answered with a message from days ago.
    public static readonly TimeSpan IdempotencyKeyLifetime = TimeSpan.FromHours(24);

    private readonly IDbContextFactory<SendwayDbContext> _dbContextFactory;
    private readonly IDataProtector _protector;
    private readonly TimeProvider _timeProvider;
    private readonly WebhookDeliverySignal _webhookSignal;
    private readonly DeliveryQueue<MessageRecord> _queue;

    public MessageOutbox(
        IDbContextFactory<SendwayDbContext> dbContextFactory,
        IDataProtectionProvider dataProtectionProvider,
        TimeProvider timeProvider,
        WebhookDeliverySignal webhookSignal)
    {
        _dbContextFactory = dbContextFactory;
        _protector = dataProtectionProvider.CreateProtector(ProtectorPurpose);
        _timeProvider = timeProvider;
        _webhookSignal = webhookSignal;
        _queue = new DeliveryQueue<MessageRecord>(timeProvider);
    }

    public async Task<EnqueueResult> EnqueueAsync(MessageSubmission submission, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var now = _timeProvider.GetUtcNow();
        var requestHash = ComputeRequestHash(submission);

        if (submission.IdempotencyKey is not null &&
            await FindByIdempotencyKeyAsync(db, submission, requestHash, now, cancellationToken) is { } existing)
        {
            return existing;
        }

        var record = new MessageRecord
        {
            Id = Guid.NewGuid(),
            TenantId = submission.TenantId,
            Channel = submission.Channel,
            Recipient = submission.Recipient,
            Status = DeliveryStatus.Queued,
            AcceptedAt = now,
            NextAttemptAt = now,
            Payload = _protector.Protect(submission.Payload),
            IdempotencyKey = submission.IdempotencyKey,
            RequestHash = requestHash
        };

        db.MessageRecords.Add(record);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException) when (submission.IdempotencyKey is not null)
        {
            // The unique (TenantId, IdempotencyKey) index rejected the insert: a concurrent request
            // with the same key won the race between the lookup above and this save. That request's
            // message is the one to answer with.
            db.Entry(record).State = EntityState.Detached;

            var winner = await FindByIdempotencyKeyAsync(db, submission, requestHash, now, cancellationToken);
            if (winner is null)
            {
                // Not a key collision after all — let the original storage failure surface.
                throw;
            }

            return winner;
        }

        return new EnqueueResult(EnqueueOutcome.Accepted, record);
    }

    public async Task<MessageRecord?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        return await db.MessageRecords.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id, cancellationToken);
    }

    public async Task<ClaimedMessage?> ClaimNextAsync(TimeSpan lease, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var record = await _queue.ClaimNextAsync(db, lease, cancellationToken);

        return record is null
            ? null
            : new ClaimedMessage(record.Id, record.TenantId, record.Channel, record.Payload!, record.AttemptCount);
    }

    public string ReadPayload(ClaimedMessage message)
    {
        try
        {
            return _protector.Unprotect(message.ProtectedPayload);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException(
                "The message content could not be decrypted: the key that protected it is not in this instance's Data Protection key ring.",
                ex);
        }
    }

    public Task MarkSentAsync(ClaimedMessage claim, CancellationToken cancellationToken = default) =>
        CompleteAsync(claim, DeliveryStatus.Sent, error: null, cancellationToken);

    public Task MarkFailedAsync(ClaimedMessage claim, string error, CancellationToken cancellationToken = default) =>
        CompleteAsync(claim, DeliveryStatus.Failed, error, cancellationToken);

    public async Task ScheduleRetryAsync(ClaimedMessage claim, string error, TimeSpan delay, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        await _queue.ScheduleRetryAsync(db, claim, error, delay, cancellationToken);
    }

    public async Task ReleaseAsync(ClaimedMessage claim, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        await _queue.ReleaseAsync(db, claim, cancellationToken);
    }

    public async Task<QueueStats> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await _queue.GetStatsAsync(db, cancellationToken);
    }

    // Recording the final outcome and queueing the event that announces it happen in one
    // transaction: there is never an outcome without its event, nor an event for an outcome that
    // wasn't recorded.
    private async Task CompleteAsync(ClaimedMessage claim, DeliveryStatus status, string? error, CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var recorded = await _queue.CompleteAsync(db, claim, status, error, cancellationToken) == 1;
        var eventQueued = false;

        if (recorded && await db.WebhookEndpoints.AnyAsync(e => e.TenantId == claim.TenantId, cancellationToken))
        {
            var record = await db.MessageRecords.AsNoTracking().FirstAsync(m => m.Id == claim.Id, cancellationToken);
            var now = _timeProvider.GetUtcNow();

            db.WebhookDeliveries.Add(new WebhookDelivery
            {
                Id = Guid.NewGuid(),
                TenantId = record.TenantId,
                MessageId = record.Id,
                EventType = MessageEvent.TypeFor(status),
                Status = DeliveryStatus.Queued,
                CreatedAt = now,
                NextAttemptAt = now,
                Payload = MessageEvent.Serialize(record, now)
            });

            await db.SaveChangesAsync(cancellationToken);
            eventQueued = true;
        }

        await transaction.CommitAsync(cancellationToken);

        if (eventQueued)
        {
            _webhookSignal.Notify();
        }
    }

    private static async Task<EnqueueResult?> FindByIdempotencyKeyAsync(
        SendwayDbContext db,
        MessageSubmission submission,
        string requestHash,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var existing = await db.MessageRecords
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.TenantId == submission.TenantId && m.IdempotencyKey == submission.IdempotencyKey, cancellationToken);

        if (existing is null)
        {
            return null;
        }

        if (now - existing.AcceptedAt >= IdempotencyKeyLifetime)
        {
            // The key's window has passed: unbind it so this request is accepted as a new message.
            await db.MessageRecords
                .Where(m => m.Id == existing.Id)
                .ExecuteUpdateAsync(update => update.SetProperty(m => m.IdempotencyKey, (string?)null), cancellationToken);

            return null;
        }

        var outcome = existing.RequestHash == requestHash ? EnqueueOutcome.Replayed : EnqueueOutcome.KeyReused;
        return new EnqueueResult(outcome, existing);
    }

    private static string ComputeRequestHash(MessageSubmission submission)
    {
        var bytes = Encoding.UTF8.GetBytes(submission.Channel + "\n" + submission.Payload);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }
}
