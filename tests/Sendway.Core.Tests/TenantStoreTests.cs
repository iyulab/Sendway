using Sendway.Core;
using Xunit;

namespace Sendway.Core.Tests;

public class TenantStoreTests : IAsyncLifetime
{
    private readonly TestDatabase _database = new();
    private ITenantStore _store = null!;

    public async Task InitializeAsync()
    {
        await _database.CreateAsync();
        _store = new TenantStore(_database.Factory);
    }

    public Task DisposeAsync()
    {
        _database.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task CreateAsync_ThenGetByApiKeyHash_ReturnsCreatedTenant()
    {
        var tenant = await _store.CreateAsync("billing-service", "hash-abc");

        var found = await _store.FindByApiKeyHashAsync("hash-abc", DateTimeOffset.UtcNow);

        Assert.NotNull(found);
        Assert.Equal(tenant.Id, found!.Tenant.Id);
        Assert.Equal("billing-service", found.Tenant.Name);
        Assert.True(found.Tenant.Active);
        Assert.Null(found.ExpiresAt);
    }

    [Fact]
    public async Task FindByApiKeyHashAsync_UnknownHash_ReturnsNull()
    {
        var found = await _store.FindByApiKeyHashAsync("does-not-exist", DateTimeOffset.UtcNow);

        Assert.Null(found);
    }

    [Fact]
    public async Task SetActiveAsync_ExistingTenant_UpdatesActiveFlagAndReturnsTrue()
    {
        var tenant = await _store.CreateAsync("billing-service", "hash-abc");

        var updated = await _store.SetActiveAsync(tenant.Id, active: false);

        Assert.True(updated);
        var found = await _store.GetByIdAsync(tenant.Id);
        Assert.False(found!.Active);
    }

    [Fact]
    public async Task SetActiveAsync_UnknownId_ReturnsFalse()
    {
        var updated = await _store.SetActiveAsync(Guid.NewGuid(), active: false);

        Assert.False(updated);
    }

    [Fact]
    public async Task RotateApiKeyAsync_WithoutOverlap_EndsTheOldKeyAtOnce()
    {
        var tenant = await _store.CreateAsync("billing-service", "hash-old");
        var now = DateTimeOffset.UtcNow;

        var updated = await _store.RotateApiKeyAsync(tenant.Id, "hash-new", previousKeyExpiresAt: null);

        Assert.True(updated);
        Assert.Null(await _store.FindByApiKeyHashAsync("hash-old", now));
        Assert.Null((await _store.FindByApiKeyHashAsync("hash-new", now))!.ExpiresAt);
    }

    [Fact]
    public async Task RotateApiKeyAsync_WithOverlap_KeepsTheOldKeyUntilItExpires()
    {
        var tenant = await _store.CreateAsync("billing-service", "hash-old");
        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.AddHours(1);

        await _store.RotateApiKeyAsync(tenant.Id, "hash-new", expiresAt);

        var during = await _store.FindByApiKeyHashAsync("hash-old", now);
        Assert.Equal(tenant.Id, during!.Tenant.Id);
        Assert.Equal(expiresAt, during.ExpiresAt!.Value, TimeSpan.FromMilliseconds(1));
        Assert.Null(await _store.FindByApiKeyHashAsync("hash-old", expiresAt.AddSeconds(1)));
        Assert.Null((await _store.FindByApiKeyHashAsync("hash-new", now))!.ExpiresAt);
    }

    [Fact]
    public async Task RotateApiKeyAsync_DuringAnOverlap_EndsTheOlderPreviousKey()
    {
        var tenant = await _store.CreateAsync("billing-service", "hash-1");
        var now = DateTimeOffset.UtcNow;

        await _store.RotateApiKeyAsync(tenant.Id, "hash-2", now.AddHours(1));
        await _store.RotateApiKeyAsync(tenant.Id, "hash-3", now.AddHours(1));

        Assert.Null(await _store.FindByApiKeyHashAsync("hash-1", now));
        Assert.NotNull(await _store.FindByApiKeyHashAsync("hash-2", now));
        Assert.NotNull(await _store.FindByApiKeyHashAsync("hash-3", now));
    }

    [Fact]
    public async Task RotateApiKeyAsync_UnknownId_ReturnsFalse()
    {
        Assert.False(await _store.RotateApiKeyAsync(Guid.NewGuid(), "hash-new", previousKeyExpiresAt: null));
    }

    [Fact]
    public async Task RevokePreviousApiKeyAsync_EndsTheOverlapNow()
    {
        var tenant = await _store.CreateAsync("billing-service", "hash-old");
        var now = DateTimeOffset.UtcNow;
        await _store.RotateApiKeyAsync(tenant.Id, "hash-new", now.AddHours(1));

        Assert.True(await _store.RevokePreviousApiKeyAsync(tenant.Id, now));

        Assert.Null(await _store.FindByApiKeyHashAsync("hash-old", now));
        Assert.NotNull(await _store.FindByApiKeyHashAsync("hash-new", now));
        Assert.False(await _store.RevokePreviousApiKeyAsync(tenant.Id, now));
    }

    [Fact]
    public async Task RevokePreviousApiKeyAsync_AfterTheOverlapEnded_ReturnsFalse()
    {
        var tenant = await _store.CreateAsync("billing-service", "hash-old");
        var now = DateTimeOffset.UtcNow;
        await _store.RotateApiKeyAsync(tenant.Id, "hash-new", now.AddHours(1));

        Assert.False(await _store.RevokePreviousApiKeyAsync(tenant.Id, now.AddHours(2)));
    }

    [Fact]
    public async Task ListAsync_ReturnsAllCreatedTenants()
    {
        await _store.CreateAsync("billing-service", "hash-1");
        await _store.CreateAsync("inventory-service", "hash-2");

        var all = await _store.ListAsync();

        Assert.Equal(2, all.Count);
    }
}
