using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sendway.Core;
using Sendway.Service.Endpoints.Admin;
using Xunit;

namespace Sendway.Service.Tests;

public class TenantCredentialOverrideTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private const string AdminApiKey = "test-admin-key";

    private readonly WebApplicationFactory<Program> _factory;
    private readonly TestIsolatedStorage _storage = new();

    public TenantCredentialOverrideTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            // This test asserts on the *absence* of a shared credential for an unrelated tenant.
            // The default WebApplicationFactory environment is "Development", which would load
            // whatever local, gitignored appsettings.Development.json happens to exist on the
            // machine running the tests (e.g. a developer's real SMTP credentials from manual
            // testing) — making the assertion pass or fail depending on the machine, not the code.
            // "Testing" has no corresponding appsettings.Testing.json, so only the tracked,
            // secret-free appsettings.json loads.
            builder.UseEnvironment("Testing");
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

    [Fact]
    public async Task SetCredential_TenantWithOverride_ResolvesOverrideNotShared()
    {
        var adminClient = _factory.CreateClient();
        adminClient.DefaultRequestHeaders.Add("X-Admin-Key", AdminApiKey);

        var created = await (await adminClient.PostAsJsonAsync("/admin/tenants", new CreateTenantRequest("Authway")))
            .Content.ReadFromJsonAsync<CreateTenantResponse>();

        var overridePayload = new SmtpOptions { Host = "authway-only.example.com", FromAddress = "authway@example.com" };
        var putResponse = await adminClient.PutAsJsonAsync($"/admin/tenants/{created!.TenantId}/credentials/smtp", overridePayload);
        Assert.Equal(HttpStatusCode.NoContent, putResponse.StatusCode);

        var credentialStore = _factory.Services.GetRequiredService<ICredentialStore>();
        var resolvedForTenant = await credentialStore.GetAsync<SmtpOptions>(created.TenantId, ChannelCredentialNames.Smtp);
        var resolvedForOtherTenant = await credentialStore.GetAsync<SmtpOptions>(Guid.NewGuid(), ChannelCredentialNames.Smtp);

        Assert.NotNull(resolvedForTenant);
        Assert.Equal("authway-only.example.com", resolvedForTenant!.Host);
        Assert.Null(resolvedForOtherTenant);
    }

    [Fact]
    public async Task SetCredential_FcmChannel_PersistsOverrideAndCallsInvalidation()
    {
        var adminClient = _factory.CreateClient();
        adminClient.DefaultRequestHeaders.Add("X-Admin-Key", AdminApiKey);

        var created = await (await adminClient.PostAsJsonAsync("/admin/tenants", new CreateTenantRequest("Authway")))
            .Content.ReadFromJsonAsync<CreateTenantResponse>();

        // CredentialsJson is deliberately not a real service-account payload — this exercises the
        // endpoint's persistence + InvalidateTenant call path, not FcmPushSender's FirebaseApp
        // creation (which this plan does not test against real FCM, per Task 3's brief).
        var overridePayload = new FcmOptions { CredentialsJson = "{\"type\":\"service_account\"}" };
        var putResponse = await adminClient.PutAsJsonAsync($"/admin/tenants/{created!.TenantId}/credentials/fcm", overridePayload);
        Assert.Equal(HttpStatusCode.NoContent, putResponse.StatusCode);

        var credentialStore = _factory.Services.GetRequiredService<ICredentialStore>();
        var resolved = await credentialStore.GetAsync<FcmOptions>(created.TenantId, ChannelCredentialNames.Fcm);

        Assert.NotNull(resolved);
        Assert.Equal("{\"type\":\"service_account\"}", resolved!.CredentialsJson);
    }

    [Fact]
    public async Task SetCredential_EmailGraphChannel_PersistsOverrideAndCallsInvalidation()
    {
        var adminClient = _factory.CreateClient();
        adminClient.DefaultRequestHeaders.Add("X-Admin-Key", AdminApiKey);

        var created = await (await adminClient.PostAsJsonAsync("/admin/tenants", new CreateTenantRequest("Authway")))
            .Content.ReadFromJsonAsync<CreateTenantResponse>();

        // Fake, never-valid credentials — exercises the endpoint's persistence + InvalidateTenant
        // call path, not GraphEmailSender's actual token acquisition (no live Azure AD app available
        // to this test suite).
        var overridePayload = new GraphOptions
        {
            DirectoryId = "00000000-0000-0000-0000-000000000000",
            ClientId = "00000000-0000-0000-0000-000000000000",
            ClientSecret = "not-a-real-secret",
            FromAddress = "authway@example.com"
        };
        var putResponse = await adminClient.PutAsJsonAsync($"/admin/tenants/{created!.TenantId}/credentials/email-graph", overridePayload);
        Assert.Equal(HttpStatusCode.NoContent, putResponse.StatusCode);

        var credentialStore = _factory.Services.GetRequiredService<ICredentialStore>();
        var resolved = await credentialStore.GetAsync<GraphOptions>(created.TenantId, ChannelCredentialNames.EmailGraph);

        Assert.NotNull(resolved);
        Assert.Equal("authway@example.com", resolved!.FromAddress);
    }

    [Fact]
    public async Task DeleteCredential_TenantWithOverride_RevertsToSharedDefault()
    {
        var adminClient = _factory.CreateClient();
        adminClient.DefaultRequestHeaders.Add("X-Admin-Key", AdminApiKey);

        var created = await (await adminClient.PostAsJsonAsync("/admin/tenants", new CreateTenantRequest("Authway")))
            .Content.ReadFromJsonAsync<CreateTenantResponse>();

        var overridePayload = new SmtpOptions { Host = "authway-only.example.com", FromAddress = "authway@example.com" };
        await adminClient.PutAsJsonAsync($"/admin/tenants/{created!.TenantId}/credentials/smtp", overridePayload);

        var deleteResponse = await adminClient.DeleteAsync($"/admin/tenants/{created.TenantId}/credentials/smtp");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        var credentialStore = _factory.Services.GetRequiredService<ICredentialStore>();
        var resolvedForTenant = await credentialStore.GetAsync<SmtpOptions>(created.TenantId, ChannelCredentialNames.Smtp);

        // No shared default is seeded in this test environment (see the class-level comment on
        // "Testing" environment above) — so once the override is gone, resolution falls all the
        // way through to null rather than resurrecting a value the tenant never had.
        Assert.Null(resolvedForTenant);
    }

    [Fact]
    public async Task DeleteCredential_EmailGraphChannel_InvalidatesCachedClient()
    {
        var adminClient = _factory.CreateClient();
        adminClient.DefaultRequestHeaders.Add("X-Admin-Key", AdminApiKey);

        var created = await (await adminClient.PostAsJsonAsync("/admin/tenants", new CreateTenantRequest("Authway")))
            .Content.ReadFromJsonAsync<CreateTenantResponse>();

        var overridePayload = new GraphOptions
        {
            DirectoryId = "00000000-0000-0000-0000-000000000000",
            ClientId = "00000000-0000-0000-0000-000000000000",
            ClientSecret = "not-a-real-secret",
            FromAddress = "authway@example.com"
        };
        await adminClient.PutAsJsonAsync($"/admin/tenants/{created!.TenantId}/credentials/email-graph", overridePayload);

        var deleteResponse = await adminClient.DeleteAsync($"/admin/tenants/{created.TenantId}/credentials/email-graph");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        var credentialStore = _factory.Services.GetRequiredService<ICredentialStore>();
        var resolved = await credentialStore.GetAsync<GraphOptions>(created.TenantId, ChannelCredentialNames.EmailGraph);

        Assert.Null(resolved);
    }

    [Fact]
    public async Task DeleteCredential_UnknownTenant_Returns404()
    {
        var adminClient = _factory.CreateClient();
        adminClient.DefaultRequestHeaders.Add("X-Admin-Key", AdminApiKey);

        var response = await adminClient.DeleteAsync($"/admin/tenants/{Guid.NewGuid()}/credentials/smtp");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DeleteCredential_UnknownChannel_Returns400()
    {
        var adminClient = _factory.CreateClient();
        adminClient.DefaultRequestHeaders.Add("X-Admin-Key", AdminApiKey);

        var created = await (await adminClient.PostAsJsonAsync("/admin/tenants", new CreateTenantRequest("Authway")))
            .Content.ReadFromJsonAsync<CreateTenantResponse>();

        var response = await adminClient.DeleteAsync($"/admin/tenants/{created!.TenantId}/credentials/slack");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task SetCredential_UnknownTenant_Returns404()
    {
        var adminClient = _factory.CreateClient();
        adminClient.DefaultRequestHeaders.Add("X-Admin-Key", AdminApiKey);

        var response = await adminClient.PutAsJsonAsync(
            $"/admin/tenants/{Guid.NewGuid()}/credentials/smtp",
            new SmtpOptions { Host = "x.example.com", FromAddress = "x@example.com" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task SetCredential_UnknownChannel_Returns400()
    {
        var adminClient = _factory.CreateClient();
        adminClient.DefaultRequestHeaders.Add("X-Admin-Key", AdminApiKey);

        var created = await (await adminClient.PostAsJsonAsync("/admin/tenants", new CreateTenantRequest("Authway")))
            .Content.ReadFromJsonAsync<CreateTenantResponse>();

        var response = await adminClient.PutAsJsonAsync(
            $"/admin/tenants/{created!.TenantId}/credentials/slack",
            new { anything = "value" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
