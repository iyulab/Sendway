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

        // Checked after the replay lookup, so a retry of an accepted request is answered the same way
        // even if the credential has gone since.
        if (!await CanSendAsync(db, submission.TenantId, submission.Channel, cancellationToken))
        {
            return new EnqueueResult(EnqueueOutcome.ChannelNotConfigured, null);
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
            RequestHash = requestHash,
            ExpiresAt = submission.ExpiresAt
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
            : new ClaimedMessage(record.Id, record.TenantId, record.Channel, record.Payload!, record.AttemptCount, record.ExpiresAt);
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
        CompleteAsync(claim, DeliveryStatus.Sent, error: null, reason: null, cancellationToken);

    public Task MarkFailedAsync(ClaimedMessage claim, string error, string? reason = null, CancellationToken cancellationToken = default) =>
        CompleteAsync(claim, DeliveryStatus.Failed, error, reason, cancellationToken);

    public async Task ScheduleRetryAsync(ClaimedMessage claim, string error, TimeSpan delay, CancellationToken cancellationToken = default)
    {
        // Never later than the message's deadline: the attempt made then ends it as expired, rather
        // than leaving it Queued long after it stopped being worth sending.
        if (claim.ExpiresAt is { } expiresAt)
        {
            var untilExpiry = expiresAt - _timeProvider.GetUtcNow();
            delay = untilExpiry < delay ? (untilExpiry > TimeSpan.Zero ? untilExpiry : TimeSpan.Zero) : delay;
        }

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

    public async Task<IReadOnlyList<ChannelDeliveryState>> GetChannelStatesAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var now = _timeProvider.GetUtcNow();

        var unfinished = await db.MessageRecords
            .AsNoTracking()
            .Where(m => m.Status == DeliveryStatus.Queued || m.Status == DeliveryStatus.Sending)
            .GroupBy(m => new { m.TenantId, m.Channel })
            .Select(g => new { g.Key.TenantId, g.Key.Channel, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var unsendable = new Dictionary<string, int>();
        foreach (var group in unfinished)
        {
            if (!await CanSendAsync(db, group.TenantId, group.Channel, cancellationToken))
            {
                unsendable[group.Channel] = unsendable.GetValueOrDefault(group.Channel) + group.Count;
            }
        }

        // A message that has failed and waits to be tried again, or is being tried again now.
        var retrying = await db.MessageRecords
            .AsNoTracking()
            .Where(m => (m.Status == DeliveryStatus.Queued && m.AttemptCount >= 1) ||
                        (m.Status == DeliveryStatus.Sending && m.AttemptCount >= 2))
            .Select(m => new { m.Channel, m.AcceptedAt })
            .ToListAsync(cancellationToken);

        var failingFor = new Dictionary<string, TimeSpan>();
        foreach (var channel in retrying.Select(m => m.Channel).Distinct())
        {
            var lastSent = await db.MessageRecords
                .Where(m => m.Status == DeliveryStatus.Sent && m.Channel == channel)
                .MaxAsync(m => m.CompletedAt, cancellationToken);

            // Messages accepted after the last success that went out are what tells whether the channel
            // works now; an older one stuck on its own recipient while later ones get through does not.
            var oldestSinceSuccess = retrying
                .Where(m => m.Channel == channel && (lastSent is null || m.AcceptedAt > lastSent))
                .Select(m => (DateTimeOffset?)m.AcceptedAt)
                .Min();

            if (oldestSinceSuccess is { } since)
            {
                failingFor[channel] = now - since;
            }
        }

        return unsendable.Keys.Union(failingFor.Keys)
            .Order()
            .Select(channel => new ChannelDeliveryState(
                channel,
                unsendable.GetValueOrDefault(channel),
                failingFor.TryGetValue(channel, out var age) ? age : null))
            .ToList();
    }

    // Whether a credential that lets the tenant send on the channel is stored — its own or the shared
    // one. Only that a row exists is asked: decrypting here would turn a lost key ring into a refusal
    // to accept, where it is meant to surface as failed deliveries.
    private static Task<bool> CanSendAsync(SendwayDbContext db, Guid tenantId, string channel, CancellationToken cancellationToken)
    {
        var credentials = MessageChannels.CredentialsFor(channel);

        return db.ChannelCredentials.AnyAsync(
            c => credentials.Contains(c.Channel) && (c.TenantId == tenantId || c.TenantId == Guid.Empty),
            cancellationToken);
    }

    // Recording the final outcome and queueing the event that announces it happen in one
    // transaction: there is never an outcome without its event, nor an event for an outcome that
    // wasn't recorded.
    private async Task CompleteAsync(ClaimedMessage claim, DeliveryStatus status, string? error, string? reason, CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var recorded = await _queue.CompleteAsync(db, claim, status, error, cancellationToken) == 1;

        // In the same transaction as the outcome, and before the event below reads the row back.
        if (recorded && reason is not null)
        {
            await db.MessageRecords
                .Where(m => m.Id == claim.Id)
                .ExecuteUpdateAsync(update => update.SetProperty(m => m.FailureReason, reason), cancellationToken);
        }
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
        // The deadline is part of the request; appended only when present, so requests without one
        // keep the fingerprint they had before deadlines existed.
        var text = submission.Channel + "\n" + submission.Payload;
        if (submission.ExpiresAt is { } expiresAt)
        {
            text += "\n" + expiresAt.UtcDateTime.ToString("O");
        }

        var bytes = Encoding.UTF8.GetBytes(text);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }
}
