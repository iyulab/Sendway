using Microsoft.EntityFrameworkCore;

namespace Sendway.Core;

// How much each tenant actually sends: the last message it got out, ever, and per channel how many of
// its messages ended sent or failed since a given time. For an operator to see which calling services
// use the instance and where failures come from — counts only, no recipients, no content, no errors
// (a provider's error text can carry a recipient's address).
public interface IMessageUsage
{
    Task<IReadOnlyList<TenantUsage>> GetAsync(DateTimeOffset since, CancellationToken cancellationToken = default);
}

public sealed record TenantUsage(
    Guid TenantId,
    string Name,
    bool Active,
    DateTimeOffset? LastSentAt,
    IReadOnlyDictionary<string, ChannelUsage> Channels);

public sealed record ChannelUsage(int Sent, int Failed);

public sealed class MessageUsage(IDbContextFactory<SendwayDbContext> dbContextFactory) : IMessageUsage
{
    public async Task<IReadOnlyList<TenantUsage>> GetAsync(DateTimeOffset since, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        var tenants = await db.Tenants
            .AsNoTracking()
            .OrderBy(t => t.CreatedAt)
            .Select(t => new { t.Id, t.Name, t.Active })
            .ToListAsync(cancellationToken);

        var finished = await db.MessageRecords
            .AsNoTracking()
            .Where(m => (m.Status == DeliveryStatus.Sent || m.Status == DeliveryStatus.Failed) && m.CompletedAt >= since)
            .GroupBy(m => new { m.TenantId, m.Channel, m.Status })
            .Select(g => new { g.Key.TenantId, g.Key.Channel, g.Key.Status, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var lastSent = await db.MessageRecords
            .AsNoTracking()
            .Where(m => m.Status == DeliveryStatus.Sent)
            .GroupBy(m => m.TenantId)
            .Select(g => new { TenantId = g.Key, At = g.Max(m => m.CompletedAt) })
            .ToDictionaryAsync(x => x.TenantId, x => x.At, cancellationToken);

        return tenants
            .Select(t => new TenantUsage(
                t.Id,
                t.Name,
                t.Active,
                lastSent.GetValueOrDefault(t.Id),
                finished
                    .Where(f => f.TenantId == t.Id)
                    .GroupBy(f => f.Channel)
                    .OrderBy(g => g.Key)
                    .ToDictionary(
                        g => g.Key,
                        g => new ChannelUsage(
                            g.Where(f => f.Status == DeliveryStatus.Sent).Sum(f => f.Count),
                            g.Where(f => f.Status == DeliveryStatus.Failed).Sum(f => f.Count)))))
            .ToList();
    }
}
