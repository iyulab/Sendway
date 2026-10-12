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

    public MessageOutbox(
        IDbContextFactory<SendwayDbContext> dbContextFactory,
        IDataProtectionProvider dataProtectionProvider,
        TimeProvider timeProvider)
    {
        _dbContextFactory = dbContextFactory;
        _protector = dataProtectionProvider.CreateProtector(ProtectorPurpose);
        _timeProvider = timeProvider;
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
            Status = MessageDeliveryStatus.Queued,
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

        while (true)
        {
            DateTimeOffset? now = _timeProvider.GetUtcNow();

            var candidate = await db.MessageRecords
                .AsNoTracking()
                .Where(m => (m.Status == MessageDeliveryStatus.Queued || m.Status == MessageDeliveryStatus.Sending) && m.NextAttemptAt <= now)
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

            var claimed = await db.MessageRecords
                .Where(m => m.Id == candidate.Id && m.AttemptCount == candidate.AttemptCount)
                .ExecuteUpdateAsync(
                    update => update
                        .SetProperty(m => m.Status, MessageDeliveryStatus.Sending)
                        .SetProperty(m => m.AttemptCount, attempt)
                        .SetProperty(m => m.NextAttemptAt, leaseExpiry),
                    cancellationToken);

            if (claimed == 0)
            {
                continue;
            }

            var record = await db.MessageRecords.AsNoTracking().FirstAsync(m => m.Id == candidate.Id, cancellationToken);

            return new ClaimedMessage(record.Id, record.TenantId, record.Channel, _protector.Unprotect(record.Payload!), attempt);
        }
    }

    public Task MarkSentAsync(ClaimedMessage message, CancellationToken cancellationToken = default) =>
        CompleteAsync(message, MessageDeliveryStatus.Sent, error: null, cancellationToken);

    public Task MarkFailedAsync(ClaimedMessage message, string error, CancellationToken cancellationToken = default) =>
        CompleteAsync(message, MessageDeliveryStatus.Failed, error, cancellationToken);

    public async Task ScheduleRetryAsync(ClaimedMessage message, string error, TimeSpan delay, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        DateTimeOffset? nextAttemptAt = _timeProvider.GetUtcNow() + delay;

        await OwnedAttempt(db, message).ExecuteUpdateAsync(
            update => update
                .SetProperty(m => m.Status, MessageDeliveryStatus.Queued)
                .SetProperty(m => m.Error, error)
                .SetProperty(m => m.NextAttemptAt, nextAttemptAt),
            cancellationToken);
    }

    public async Task ReleaseAsync(ClaimedMessage message, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        DateTimeOffset? now = _timeProvider.GetUtcNow();
        var previousAttemptCount = message.AttemptCount - 1;

        await OwnedAttempt(db, message).ExecuteUpdateAsync(
            update => update
                .SetProperty(m => m.Status, MessageDeliveryStatus.Queued)
                .SetProperty(m => m.AttemptCount, previousAttemptCount)
                .SetProperty(m => m.NextAttemptAt, now),
            cancellationToken);
    }

    private async Task CompleteAsync(ClaimedMessage message, MessageDeliveryStatus status, string? error, CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        DateTimeOffset? completedAt = _timeProvider.GetUtcNow();

        await OwnedAttempt(db, message).ExecuteUpdateAsync(
            update => update
                .SetProperty(m => m.Status, status)
                .SetProperty(m => m.Error, error)
                .SetProperty(m => m.CompletedAt, completedAt)
                .SetProperty(m => m.NextAttemptAt, (DateTimeOffset?)null)
                .SetProperty(m => m.Payload, (string?)null),
            cancellationToken);
    }

    // Scopes an outcome update to the attempt that was claimed. If the lease expired and another
    // worker has since claimed the message, the attempt count has moved on and the stale outcome
    // is dropped instead of overwriting the newer attempt's state.
    private static IQueryable<MessageRecord> OwnedAttempt(SendwayDbContext db, ClaimedMessage message) =>
        db.MessageRecords.Where(m =>
            m.Id == message.Id &&
            m.Status == MessageDeliveryStatus.Sending &&
            m.AttemptCount == message.AttemptCount);

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
