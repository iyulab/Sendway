using Microsoft.EntityFrameworkCore;

namespace Sendway.Core;

public sealed class MessageStatusStore : IMessageStatusStore
{
    private readonly IDbContextFactory<SendwayDbContext> _dbContextFactory;
    private readonly TimeProvider _timeProvider;

    public MessageStatusStore(IDbContextFactory<SendwayDbContext> dbContextFactory, TimeProvider timeProvider)
    {
        _dbContextFactory = dbContextFactory;
        _timeProvider = timeProvider;
    }

    public async Task<Guid> RecordAsync(
        Guid tenantId,
        string channel,
        string recipient,
        MessageDeliveryStatus status,
        string? error,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var record = new MessageRecord
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Channel = channel,
            Recipient = recipient,
            Status = status,
            Error = error,
            SentAt = _timeProvider.GetUtcNow()
        };

        db.MessageRecords.Add(record);
        await db.SaveChangesAsync(cancellationToken);

        return record.Id;
    }

    public async Task<MessageRecord?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        return await db.MessageRecords.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id, cancellationToken);
    }
}
