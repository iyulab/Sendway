using Sendway.Core;
using Xunit;

namespace Sendway.Core.Tests;

public class AuditLogTests : IAsyncLifetime
{
    private readonly TestDatabase _database = new();
    private IAuditLog _log = null!;

    public async Task InitializeAsync()
    {
        await _database.CreateAsync();
        _log = new AuditLog(_database.Factory);
    }

    public Task DisposeAsync()
    {
        _database.Dispose();
        return Task.CompletedTask;
    }

    private static AuditEvent Event(DateTimeOffset at, Guid? tenantId = null, string action = "test.event") => new()
    {
        Id = Guid.NewGuid(),
        OccurredAt = at,
        Actor = "test",
        Action = action,
        TenantId = tenantId
    };

    [Fact]
    public async Task ListAsync_ReturnsNewestFirst()
    {
        var start = DateTimeOffset.UtcNow;
        await _log.AppendAsync(Event(start, action: "first"));
        await _log.AppendAsync(Event(start.AddSeconds(1), action: "second"));

        var events = await _log.ListAsync(tenantId: null, before: null, limit: 10);

        Assert.Equal(["second", "first"], events.Select(e => e.Action));
    }

    [Fact]
    public async Task ListAsync_ForATenant_LeavesOthersOut()
    {
        var tenant = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await _log.AppendAsync(Event(now, tenant));
        await _log.AppendAsync(Event(now, Guid.NewGuid()));
        await _log.AppendAsync(Event(now, tenantId: null));

        var events = await _log.ListAsync(tenant, before: null, limit: 10);

        Assert.Equal(tenant, Assert.Single(events).TenantId);
    }

    // The cursor is taken from the stored event, so it compares equal to what the database holds
    // even where the database keeps time at a coarser precision than the clock.
    [Fact]
    public async Task ListAsync_PagingFromTheLastEvent_VisitsEveryEventOnce_EvenWithinOneInstant()
    {
        var instant = DateTimeOffset.UtcNow;
        for (var i = 0; i < 5; i++)
        {
            await _log.AppendAsync(Event(instant));
        }

        await _log.AppendAsync(Event(instant.AddSeconds(-1)));

        var seen = new List<Guid>();
        var page = await _log.ListAsync(null, null, 2);
        while (page.Count > 0)
        {
            seen.AddRange(page.Select(e => e.Id));
            var last = (await _log.GetAsync(page[^1].Id))!;
            page = await _log.ListAsync(null, new AuditCursor(last.OccurredAt, last.Id), 2);
        }

        Assert.Equal(6, seen.Count);
        Assert.Equal(6, seen.Distinct().Count());
    }
}
