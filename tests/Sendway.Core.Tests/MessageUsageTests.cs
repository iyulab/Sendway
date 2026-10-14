using Sendway.Core;
using Xunit;

namespace Sendway.Core.Tests;

public class MessageUsageTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    private readonly TestDatabase _database = new();
    private MessageUsage _usage = null!;

    public async Task InitializeAsync()
    {
        await _database.CreateAsync();
        _usage = new MessageUsage(_database.Factory);
    }

    public Task DisposeAsync()
    {
        _database.Dispose();
        return Task.CompletedTask;
    }

    private async Task<Guid> AddTenantAsync(string name, DateTimeOffset createdAt, bool active = true)
    {
        var id = Guid.NewGuid();
        await using var db = await _database.Factory.CreateDbContextAsync();
        db.Tenants.Add(new Tenant { Id = id, Name = name, ApiKeyHash = Guid.NewGuid().ToString("N"), Active = active, CreatedAt = createdAt });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task AddMessageAsync(Guid tenantId, string channel, DeliveryStatus status, DateTimeOffset? completedAt)
    {
        await using var db = await _database.Factory.CreateDbContextAsync();
        db.MessageRecords.Add(new MessageRecord
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Channel = channel,
            Recipient = "user@example.com",
            AcceptedAt = (completedAt ?? Now).AddSeconds(-5),
            Status = status,
            CompletedAt = completedAt,
            NextAttemptAt = completedAt is null ? Now : null
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task ListsEveryTenant_WithItsLastSendEver_AndHowItsMessagesEndedSince()
    {
        var busy = await AddTenantAsync("busy", Now.AddDays(-90));
        var quiet = await AddTenantAsync("quiet", Now.AddDays(-60));
        var idle = await AddTenantAsync("idle", Now.AddDays(-30), active: false);

        await AddMessageAsync(busy, MessageChannels.Email, DeliveryStatus.Sent, Now.AddHours(-1));
        await AddMessageAsync(busy, MessageChannels.Email, DeliveryStatus.Sent, Now.AddDays(-2));
        await AddMessageAsync(busy, MessageChannels.Email, DeliveryStatus.Failed, Now.AddDays(-3));
        await AddMessageAsync(busy, MessageChannels.Push, DeliveryStatus.Failed, Now.AddDays(-1));
        await AddMessageAsync(busy, MessageChannels.Email, DeliveryStatus.Queued, null);
        await AddMessageAsync(quiet, MessageChannels.Email, DeliveryStatus.Sent, Now.AddDays(-45));

        var usage = await _usage.GetAsync(Now.AddDays(-30));

        Assert.Equal(["busy", "quiet", "idle"], usage.Select(t => t.Name));

        var b = usage[0];
        Assert.Equal(busy, b.TenantId);
        Assert.Equal(Now.AddHours(-1), b.LastSentAt);
        Assert.Equal(new ChannelUsage(2, 1), b.Channels[MessageChannels.Email]);
        Assert.Equal(new ChannelUsage(0, 1), b.Channels[MessageChannels.Push]);

        // Sent before the window: counted in nothing, but still its last send.
        var q = usage[1];
        Assert.Equal(Now.AddDays(-45), q.LastSentAt);
        Assert.Empty(q.Channels);

        var i = usage[2];
        Assert.Equal(idle, i.TenantId);
        Assert.False(i.Active);
        Assert.Null(i.LastSentAt);
        Assert.Empty(i.Channels);
    }
}
