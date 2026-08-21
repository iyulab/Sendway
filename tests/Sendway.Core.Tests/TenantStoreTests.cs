using Sendway.Core;
using Xunit;

namespace Sendway.Core.Tests;

public class TenantStoreTests : IAsyncLifetime
{
    private readonly SqliteTempDatabase _database = new();
    private ITenantStore _store = null!;

    public async Task InitializeAsync()
    {
        await _database.EnsureCreatedAsync();
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
        var tenant = await _store.CreateAsync("Authway", "hash-abc");

        var found = await _store.GetByApiKeyHashAsync("hash-abc");

        Assert.NotNull(found);
        Assert.Equal(tenant.Id, found!.Id);
        Assert.Equal("Authway", found.Name);
        Assert.True(found.Active);
    }

    [Fact]
    public async Task GetByApiKeyHashAsync_UnknownHash_ReturnsNull()
    {
        var found = await _store.GetByApiKeyHashAsync("does-not-exist");

        Assert.Null(found);
    }

    [Fact]
    public async Task SetActiveAsync_ExistingTenant_UpdatesActiveFlagAndReturnsTrue()
    {
        var tenant = await _store.CreateAsync("Authway", "hash-abc");

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
    public async Task SetApiKeyHashAsync_RotatesKeyAndInvalidatesOldHash()
    {
        var tenant = await _store.CreateAsync("Authway", "hash-old");

        var updated = await _store.SetApiKeyHashAsync(tenant.Id, "hash-new");

        Assert.True(updated);
        Assert.Null(await _store.GetByApiKeyHashAsync("hash-old"));
        Assert.NotNull(await _store.GetByApiKeyHashAsync("hash-new"));
    }

    [Fact]
    public async Task ListAsync_ReturnsAllCreatedTenants()
    {
        await _store.CreateAsync("Authway", "hash-1");
        await _store.CreateAsync("yesung", "hash-2");

        var all = await _store.ListAsync();

        Assert.Equal(2, all.Count);
    }
}
