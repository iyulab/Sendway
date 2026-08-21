// tests/Sendway.Core.Tests/EncryptedCredentialStoreTests.cs
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Sendway.Core;
using Xunit;

namespace Sendway.Core.Tests;

public class EncryptedCredentialStoreTests : IAsyncLifetime
{
    private readonly SqliteTempDatabase _database = new();
    private readonly string _dpKeyPath = Path.Combine(Path.GetTempPath(), "sendway-dp-tests-" + Guid.NewGuid().ToString("N"));
    private ServiceProvider _dataProtectionServices = null!;
    private ICredentialStore _store = null!;

    public async Task InitializeAsync()
    {
        await _database.EnsureCreatedAsync();

        // Goes through the same AddDataProtection()/DI path Program.cs uses in production,
        // instead of the static DataProtectionProvider.Create(...) factory, to avoid depending
        // on exactly which DataProtection package ships that overload.
        var services = new ServiceCollection();
        services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(_dpKeyPath));
        _dataProtectionServices = services.BuildServiceProvider();

        _store = new EncryptedCredentialStore(_database.Factory, _dataProtectionServices.GetRequiredService<IDataProtectionProvider>());
    }

    public Task DisposeAsync()
    {
        _database.Dispose();
        _dataProtectionServices.Dispose();
        try
        {
            Directory.Delete(_dpKeyPath, recursive: true);
        }
        catch (IOException)
        {
        }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task GetAsync_NoRecordAtAll_ReturnsNull()
    {
        var result = await _store.GetAsync<SmtpOptions>(Guid.NewGuid(), ChannelCredentialNames.Smtp);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetAsync_OnlySharedRecord_ReturnsSharedRecordForAnyTenant()
    {
        await _store.SetAsync<SmtpOptions>(null, ChannelCredentialNames.Smtp, new SmtpOptions { Host = "shared.example.com", FromAddress = "shared@example.com" });

        var result = await _store.GetAsync<SmtpOptions>(Guid.NewGuid(), ChannelCredentialNames.Smtp);

        Assert.NotNull(result);
        Assert.Equal("shared.example.com", result!.Host);
    }

    [Fact]
    public async Task GetAsync_TenantHasOverride_ReturnsOverrideNotShared()
    {
        var tenantId = Guid.NewGuid();
        await _store.SetAsync<SmtpOptions>(null, ChannelCredentialNames.Smtp, new SmtpOptions { Host = "shared.example.com", FromAddress = "shared@example.com" });
        await _store.SetAsync<SmtpOptions>(tenantId, ChannelCredentialNames.Smtp, new SmtpOptions { Host = "tenant.example.com", FromAddress = "tenant@example.com" });

        var result = await _store.GetAsync<SmtpOptions>(tenantId, ChannelCredentialNames.Smtp);

        Assert.NotNull(result);
        Assert.Equal("tenant.example.com", result!.Host);
    }

    [Fact]
    public async Task GetAsync_OtherTenantHasOverride_UnrelatedTenantStillGetsShared()
    {
        var tenantWithOverride = Guid.NewGuid();
        var otherTenant = Guid.NewGuid();
        await _store.SetAsync<SmtpOptions>(null, ChannelCredentialNames.Smtp, new SmtpOptions { Host = "shared.example.com", FromAddress = "shared@example.com" });
        await _store.SetAsync<SmtpOptions>(tenantWithOverride, ChannelCredentialNames.Smtp, new SmtpOptions { Host = "tenant.example.com", FromAddress = "tenant@example.com" });

        var result = await _store.GetAsync<SmtpOptions>(otherTenant, ChannelCredentialNames.Smtp);

        Assert.NotNull(result);
        Assert.Equal("shared.example.com", result!.Host);
    }

    [Fact]
    public async Task GetAsync_NullTenantId_ReturnsSharedRecordDirectly()
    {
        await _store.SetAsync<SmtpOptions>(null, ChannelCredentialNames.Smtp, new SmtpOptions { Host = "shared.example.com", FromAddress = "shared@example.com" });

        var result = await _store.GetAsync<SmtpOptions>(null, ChannelCredentialNames.Smtp);

        Assert.NotNull(result);
        Assert.Equal("shared.example.com", result!.Host);
    }
}
