using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Sendway.Service.Auth;
using Sendway.Service.Endpoints.Admin;
using Xunit;

namespace Sendway.Service.Tests;

public class TenantKeyOverlapTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private const string AdminApiKey = "test-admin-key";

    private readonly WebApplicationFactory<Program> _factory;
    private readonly TestIsolatedStorage _storage = new();

    public TenantKeyOverlapTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((context, configuration) =>
            {
                _storage.Apply(context, configuration);
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Sendway:AdminApiKey"] = AdminApiKey
                });
            });
        });
    }

    public void Dispose() => _storage.Dispose();

    private HttpClient CreateAdminClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Admin-Key", AdminApiKey);
        return client;
    }

    private async Task<CreateTenantResponse> CreateTenantAsync(HttpClient admin) =>
        (await (await admin.PostAsJsonAsync("/admin/tenants", new CreateTenantRequest("billing-service")))
            .Content.ReadFromJsonAsync<CreateTenantResponse>())!;

    private static async Task<RotateTenantKeyResponse> RotateAsync(HttpClient admin, Guid tenantId, int? overlapSeconds)
    {
        var response = overlapSeconds is { } seconds
            ? await admin.PostAsJsonAsync($"/admin/tenants/{tenantId}/rotate-key", new RotateTenantKeyRequest(seconds))
            : await admin.PostAsync($"/admin/tenants/{tenantId}/rotate-key", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<RotateTenantKeyResponse>())!;
    }

    // Any authenticated tenant request will do; an unknown message id answers 404 once the key is
    // accepted and 401 when it is not.
    private async Task<HttpResponseMessage> CallWithAsync(string apiKey)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        return await client.GetAsync($"/messages/{Guid.NewGuid()}");
    }

    [Fact]
    public async Task Rotate_WithoutBody_EndsTheOldKeyAtOnce()
    {
        var admin = CreateAdminClient();
        var tenant = await CreateTenantAsync(admin);

        var rotated = await RotateAsync(admin, tenant.TenantId, overlapSeconds: null);

        Assert.Null(rotated.PreviousKeyExpiresAt);
        Assert.Equal(HttpStatusCode.Unauthorized, (await CallWithAsync(tenant.ApiKey)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await CallWithAsync(rotated.ApiKey)).StatusCode);
    }

    [Fact]
    public async Task Rotate_WithOverlap_KeepsTheOldKeyWorking_AndSaysWhenItStops()
    {
        var admin = CreateAdminClient();
        var tenant = await CreateTenantAsync(admin);
        var before = DateTimeOffset.UtcNow;

        var rotated = await RotateAsync(admin, tenant.TenantId, overlapSeconds: 3600);

        Assert.InRange(rotated.PreviousKeyExpiresAt!.Value, before.AddSeconds(3599), DateTimeOffset.UtcNow.AddSeconds(3601));

        var withOld = await CallWithAsync(tenant.ApiKey);
        Assert.Equal(HttpStatusCode.NotFound, withOld.StatusCode);
        var announced = DateTimeOffset.Parse(withOld.Headers.GetValues(TenantKeyAuthenticationHandler.KeyExpiresHeader).Single());
        Assert.Equal(rotated.PreviousKeyExpiresAt.Value, announced, TimeSpan.FromSeconds(1));

        var withNew = await CallWithAsync(rotated.ApiKey);
        Assert.Equal(HttpStatusCode.NotFound, withNew.StatusCode);
        Assert.False(withNew.Headers.Contains(TenantKeyAuthenticationHandler.KeyExpiresHeader));
    }

    [Fact]
    public async Task Rotate_DuringAnOverlap_EndsTheOldestKey()
    {
        var admin = CreateAdminClient();
        var tenant = await CreateTenantAsync(admin);

        var second = await RotateAsync(admin, tenant.TenantId, overlapSeconds: 3600);
        var third = await RotateAsync(admin, tenant.TenantId, overlapSeconds: 3600);

        Assert.Equal(HttpStatusCode.Unauthorized, (await CallWithAsync(tenant.ApiKey)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await CallWithAsync(second.ApiKey)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await CallWithAsync(third.ApiKey)).StatusCode);
    }

    [Fact]
    public async Task PreviousKey_OfADeactivatedTenant_IsRefused()
    {
        var admin = CreateAdminClient();
        var tenant = await CreateTenantAsync(admin);
        await RotateAsync(admin, tenant.TenantId, overlapSeconds: 3600);

        await admin.PostAsync($"/admin/tenants/{tenant.TenantId}/deactivate", null);

        Assert.Equal(HttpStatusCode.Unauthorized, (await CallWithAsync(tenant.ApiKey)).StatusCode);
    }

    [Fact]
    public async Task RevokePreviousKey_EndsTheOverlapNow()
    {
        var admin = CreateAdminClient();
        var tenant = await CreateTenantAsync(admin);
        var rotated = await RotateAsync(admin, tenant.TenantId, overlapSeconds: 3600);

        var revoke = await admin.DeleteAsync($"/admin/tenants/{tenant.TenantId}/previous-key");
        var again = await admin.DeleteAsync($"/admin/tenants/{tenant.TenantId}/previous-key");

        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await CallWithAsync(tenant.ApiKey)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await CallWithAsync(rotated.ApiKey)).StatusCode);
    }

    [Fact]
    public async Task ListTenants_ShowsAnOverlapInProgress()
    {
        var admin = CreateAdminClient();
        var tenant = await CreateTenantAsync(admin);
        var rotated = await RotateAsync(admin, tenant.TenantId, overlapSeconds: 3600);

        var listed = (await admin.GetFromJsonAsync<List<TenantResponse>>("/admin/tenants"))!.Single(t => t.Id == tenant.TenantId);
        Assert.Equal(rotated.PreviousKeyExpiresAt!.Value, listed.PreviousKeyExpiresAt!.Value, TimeSpan.FromMilliseconds(1));

        await admin.DeleteAsync($"/admin/tenants/{tenant.TenantId}/previous-key");
        listed = (await admin.GetFromJsonAsync<List<TenantResponse>>("/admin/tenants"))!.Single(t => t.Id == tenant.TenantId);
        Assert.Null(listed.PreviousKeyExpiresAt);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(RotateTenantKeyEndpoint.MaxPreviousKeyValidForSeconds + 1)]
    public async Task Rotate_OverlapOutOfRange_Returns400_AndKeepsTheKey(int seconds)
    {
        var admin = CreateAdminClient();
        var tenant = await CreateTenantAsync(admin);

        var response = await admin.PostAsJsonAsync($"/admin/tenants/{tenant.TenantId}/rotate-key", new RotateTenantKeyRequest(seconds));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await CallWithAsync(tenant.ApiKey)).StatusCode);
    }

    [Fact]
    public async Task RotateAndRevoke_AreAudited()
    {
        var admin = CreateAdminClient();
        var tenant = await CreateTenantAsync(admin);
        var rotated = await RotateAsync(admin, tenant.TenantId, overlapSeconds: 3600);
        await admin.DeleteAsync($"/admin/tenants/{tenant.TenantId}/previous-key");

        var events = (await admin.GetFromJsonAsync<List<AuditEventResponse>>($"/admin/audit?tenantId={tenant.TenantId}"))!;

        Assert.Equal(["tenant.previous_key_revoked", "tenant.key_rotated", "tenant.created"], events.Select(e => e.Action));
        Assert.Equal($"previous key until {rotated.PreviousKeyExpiresAt!.Value:O}", events[1].Subject);
    }
}
