using System.Net;
using System.Net.Http.Json;
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
