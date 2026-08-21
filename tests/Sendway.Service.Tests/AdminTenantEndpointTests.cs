using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Sendway.Service.Endpoints.Admin;
using Xunit;

namespace Sendway.Service.Tests;

public class AdminTenantEndpointTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private const string AdminApiKey = "test-admin-key";

    private readonly WebApplicationFactory<Program> _factory;
    private readonly TestIsolatedStorage _storage = new();

    public AdminTenantEndpointTests(WebApplicationFactory<Program> factory)
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

    [Fact]
    public async Task CreateTenant_ReturnsApiKeyOnce()
    {
        var client = CreateAdminClient();

        var response = await client.PostAsJsonAsync("/admin/tenants", new CreateTenantRequest("Authway"));
        var body = await response.Content.ReadFromJsonAsync<CreateTenantResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(body);
        Assert.Equal("Authway", body!.Name);
        Assert.StartsWith("sw_", body.ApiKey);
    }

    [Fact]
    public async Task CreateTenant_WithoutAdminKey_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/admin/tenants", new CreateTenantRequest("Authway"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CreateTenant_WithWrongAdminKey_Returns401()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Admin-Key", "wrong-key");

        var response = await client.PostAsJsonAsync("/admin/tenants", new CreateTenantRequest("Authway"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ListTenants_ReturnsCreatedTenantsWithoutApiKeys()
    {
        var client = CreateAdminClient();
        await client.PostAsJsonAsync("/admin/tenants", new CreateTenantRequest("Authway"));

        var response = await client.GetAsync("/admin/tenants");
        var body = await response.Content.ReadFromJsonAsync<List<TenantResponse>>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(body);
        Assert.Single(body!);
        Assert.Equal("Authway", body[0].Name);
        Assert.True(body[0].Active);
    }

    [Fact]
    public async Task DeactivateThenReactivate_TogglesActiveFlag()
    {
        var client = CreateAdminClient();
        var created = await (await client.PostAsJsonAsync("/admin/tenants", new CreateTenantRequest("Authway")))
            .Content.ReadFromJsonAsync<CreateTenantResponse>();

        var deactivateResponse = await client.PostAsync($"/admin/tenants/{created!.TenantId}/deactivate", content: null);
        Assert.Equal(HttpStatusCode.NoContent, deactivateResponse.StatusCode);

        var afterDeactivate = await (await client.GetAsync("/admin/tenants")).Content.ReadFromJsonAsync<List<TenantResponse>>();
        Assert.False(afterDeactivate!.Single(t => t.Id == created.TenantId).Active);

        var reactivateResponse = await client.PostAsync($"/admin/tenants/{created.TenantId}/reactivate", content: null);
        Assert.Equal(HttpStatusCode.NoContent, reactivateResponse.StatusCode);

        var afterReactivate = await (await client.GetAsync("/admin/tenants")).Content.ReadFromJsonAsync<List<TenantResponse>>();
        Assert.True(afterReactivate!.Single(t => t.Id == created.TenantId).Active);
    }

    [Fact]
    public async Task DeactivateUnknownTenant_Returns404()
    {
        var client = CreateAdminClient();

        var response = await client.PostAsync($"/admin/tenants/{Guid.NewGuid()}/deactivate", content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task RotateKey_OldKeyStopsWorkingNewKeyWorks()
    {
        var client = CreateAdminClient();
        var created = await (await client.PostAsJsonAsync("/admin/tenants", new CreateTenantRequest("Authway")))
            .Content.ReadFromJsonAsync<CreateTenantResponse>();

        var rotateResponse = await client.PostAsync($"/admin/tenants/{created!.TenantId}/rotate-key", content: null);
        var rotateBody = await rotateResponse.Content.ReadFromJsonAsync<RotateTenantKeyResponse>();

        Assert.Equal(HttpStatusCode.OK, rotateResponse.StatusCode);
        Assert.NotEqual(created.ApiKey, rotateBody!.ApiKey);

        var oldKeyClient = _factory.CreateClient();
        oldKeyClient.DefaultRequestHeaders.Add("X-Api-Key", created.ApiKey);
        var oldKeyResponse = await oldKeyClient.GetAsync($"/messages/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.Unauthorized, oldKeyResponse.StatusCode);

        var newKeyClient = _factory.CreateClient();
        newKeyClient.DefaultRequestHeaders.Add("X-Api-Key", rotateBody.ApiKey);
        var newKeyResponse = await newKeyClient.GetAsync($"/messages/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, newKeyResponse.StatusCode);
    }
}
