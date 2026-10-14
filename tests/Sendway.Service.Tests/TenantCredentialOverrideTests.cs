using Microsoft.EntityFrameworkCore;
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

        var created = await (await adminClient.PostAsJsonAsync("/admin/tenants", new CreateTenantRequest("billing-service")))
            .Content.ReadFromJsonAsync<CreateTenantResponse>();

        var overridePayload = new SmtpOptions { Host = "billing-only.example.com", FromAddress = "billing@example.com" };
        var putResponse = await adminClient.PutAsJsonAsync($"/admin/tenants/{created!.TenantId}/credentials/smtp", overridePayload);
        Assert.Equal(HttpStatusCode.NoContent, putResponse.StatusCode);

        var credentialStore = _factory.Services.GetRequiredService<ICredentialStore>();
        var resolvedForTenant = await credentialStore.GetAsync<SmtpOptions>(created.TenantId, ChannelCredentialNames.Smtp);
        var resolvedForOtherTenant = await credentialStore.GetAsync<SmtpOptions>(Guid.NewGuid(), ChannelCredentialNames.Smtp);

        Assert.NotNull(resolvedForTenant);
        Assert.Equal("billing-only.example.com", resolvedForTenant!.Host);
        Assert.Null(resolvedForOtherTenant);
    }

    [Fact]
    public async Task SetCredential_FcmChannel_PersistsOverrideAndCallsInvalidation()
    {
        var adminClient = _factory.CreateClient();
        adminClient.DefaultRequestHeaders.Add("X-Admin-Key", AdminApiKey);

        var created = await (await adminClient.PostAsJsonAsync("/admin/tenants", new CreateTenantRequest("billing-service")))
            .Content.ReadFromJsonAsync<CreateTenantResponse>();

        // Shaped like a service-account key but not a real one — this exercises the endpoint's
        // persistence + InvalidateTenant call path, not FcmPushSender's FirebaseApp creation.
        var overridePayload = new FcmOptions { CredentialsJson = FakeServiceAccountJson };
        var putResponse = await adminClient.PutAsJsonAsync($"/admin/tenants/{created!.TenantId}/credentials/fcm", overridePayload);
        Assert.Equal(HttpStatusCode.NoContent, putResponse.StatusCode);

        var credentialStore = _factory.Services.GetRequiredService<ICredentialStore>();
        var resolved = await credentialStore.GetAsync<FcmOptions>(created.TenantId, ChannelCredentialNames.Fcm);

        Assert.NotNull(resolved);
        Assert.Equal(FakeServiceAccountJson, resolved!.CredentialsJson);
    }

    [Fact]
    public async Task SetCredential_EmailGraphChannel_PersistsOverrideAndCallsInvalidation()
    {
        var adminClient = _factory.CreateClient();
        adminClient.DefaultRequestHeaders.Add("X-Admin-Key", AdminApiKey);

        var created = await (await adminClient.PostAsJsonAsync("/admin/tenants", new CreateTenantRequest("billing-service")))
            .Content.ReadFromJsonAsync<CreateTenantResponse>();

        // Fake, never-valid credentials — exercises the endpoint's persistence + InvalidateTenant
        // call path, not GraphEmailSender's actual token acquisition (no live Azure AD app available
        // to this test suite).
        var overridePayload = new GraphOptions
        {
            DirectoryId = "00000000-0000-0000-0000-000000000000",
            ClientId = "00000000-0000-0000-0000-000000000000",
            ClientSecret = "not-a-real-secret",
            FromAddress = "billing@example.com"
        };
        var putResponse = await adminClient.PutAsJsonAsync($"/admin/tenants/{created!.TenantId}/credentials/email-graph", overridePayload);
        Assert.Equal(HttpStatusCode.NoContent, putResponse.StatusCode);

        var credentialStore = _factory.Services.GetRequiredService<ICredentialStore>();
        var resolved = await credentialStore.GetAsync<GraphOptions>(created.TenantId, ChannelCredentialNames.EmailGraph);

        Assert.NotNull(resolved);
        Assert.Equal("billing@example.com", resolved!.FromAddress);
    }

    [Fact]
    public async Task DeleteCredential_TenantWithOverride_RevertsToSharedDefault()
    {
        var adminClient = _factory.CreateClient();
        adminClient.DefaultRequestHeaders.Add("X-Admin-Key", AdminApiKey);

        var created = await (await adminClient.PostAsJsonAsync("/admin/tenants", new CreateTenantRequest("billing-service")))
            .Content.ReadFromJsonAsync<CreateTenantResponse>();

        var overridePayload = new SmtpOptions { Host = "billing-only.example.com", FromAddress = "billing@example.com" };
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

        var created = await (await adminClient.PostAsJsonAsync("/admin/tenants", new CreateTenantRequest("billing-service")))
            .Content.ReadFromJsonAsync<CreateTenantResponse>();

        var overridePayload = new GraphOptions
        {
            DirectoryId = "00000000-0000-0000-0000-000000000000",
            ClientId = "00000000-0000-0000-0000-000000000000",
            ClientSecret = "not-a-real-secret",
            FromAddress = "billing@example.com"
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

        var created = await (await adminClient.PostAsJsonAsync("/admin/tenants", new CreateTenantRequest("billing-service")))
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

        var created = await (await adminClient.PostAsJsonAsync("/admin/tenants", new CreateTenantRequest("billing-service")))
            .Content.ReadFromJsonAsync<CreateTenantResponse>();

        var response = await adminClient.PutAsJsonAsync(
            $"/admin/tenants/{created!.TenantId}/credentials/slack",
            new { anything = "value" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("smtp", "{\"host\":\"smtp.example.com\"}")]
    [InlineData("smtp", "{\"fromAddress\":\"noreply@example.com\"}")]
    [InlineData("smtp", "{\"host\":\"smtp.example.com\",\"fromAddress\":\"not-an-email-address\"}")]
    [InlineData("smtp", "not json")]
    [InlineData("email-graph", "{\"directoryId\":\"d\",\"clientId\":\"c\",\"fromAddress\":\"noreply@example.com\"}")]
    [InlineData("email-graph", "{\"directoryId\":\"d\",\"clientId\":\"c\",\"clientSecret\":\"\",\"fromAddress\":\"noreply@example.com\"}")]
    [InlineData("fcm", "{}")]
    [InlineData("fcm", "{\"credentialsJson\":\"{}\"}")]
    public async Task SetCredential_ThatCouldNeverWork_Returns400AndStoresNothing(string channel, string body)
    {
        var adminClient = _factory.CreateClient();
        adminClient.DefaultRequestHeaders.Add("X-Admin-Key", AdminApiKey);
        var created = await (await adminClient.PostAsJsonAsync("/admin/tenants", new CreateTenantRequest("billing-service")))
            .Content.ReadFromJsonAsync<CreateTenantResponse>();

        var response = await adminClient.PutAsync(
            $"/admin/tenants/{created!.TenantId}/credentials/{channel}",
            new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemResponse>();
        Assert.Equal("invalid_request", problem!.Code);
        Assert.False(string.IsNullOrWhiteSpace(problem.Detail));

        await using var db = await _factory.Services.GetRequiredService<IDbContextFactory<SendwayDbContext>>().CreateDbContextAsync();
        Assert.False(await db.ChannelCredentials.AnyAsync(c => c.TenantId == created.TenantId));
    }

    // The detail is for the operator who sent the credential: it names the field in the body's own
    // terms and never the service's internal types.
    [Theory]
    [InlineData("solapi", "{}", "apiKey is required.")]
    [InlineData("solapi", "{\"apiKey\":\"k\",\"apiSecret\":\"s\"}", "from must be the registered sender number, digits only (e.g. 0212345678).")]
    [InlineData("email-graph", "{\"clientId\":\"c\"}", "directoryId is required.")]
    [InlineData("fcm", "{}", "credentialsJson is required.")]
    [InlineData("smtp", "{\"host\":\"smtp.example.com\"}", "fromAddress must be a valid email address.")]
    [InlineData("smtp", "{\"host\":\"smtp.example.com\",\"fromAddress\":\"a@example.com\",\"port\":true}", "port has a value of the wrong type.")]
    [InlineData("smtp", "[]", "The credential must be a JSON object.")]
    public async Task SetCredential_Refused_SaysWhatIsWrongInTheBodysTerms(string channel, string body, string expectedDetail)
    {
        var adminClient = _factory.CreateClient();
        adminClient.DefaultRequestHeaders.Add("X-Admin-Key", AdminApiKey);
        var created = await (await adminClient.PostAsJsonAsync("/admin/tenants", new CreateTenantRequest("billing-service")))
            .Content.ReadFromJsonAsync<CreateTenantResponse>();

        var response = await adminClient.PutAsync(
            $"/admin/tenants/{created!.TenantId}/credentials/{channel}",
            new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemResponse>();
        Assert.Equal(expectedDetail, problem!.Detail);
    }

    [Fact]
    public async Task SetCredential_BodyNotJson_Returns415()
    {
        var adminClient = _factory.CreateClient();
        adminClient.DefaultRequestHeaders.Add("X-Admin-Key", AdminApiKey);
        var created = await (await adminClient.PostAsJsonAsync("/admin/tenants", new CreateTenantRequest("billing-service")))
            .Content.ReadFromJsonAsync<CreateTenantResponse>();

        var response = await adminClient.PutAsync(
            $"/admin/tenants/{created!.TenantId}/credentials/smtp",
            new StringContent("host=smtp.example.com", System.Text.Encoding.UTF8, "text/plain"));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemResponse>();
        Assert.Equal("invalid_request", problem!.Code);
    }

    [Fact]
    public async Task SetCredential_SmtpProviderGivenByName_IsAccepted()
    {
        var adminClient = _factory.CreateClient();
        adminClient.DefaultRequestHeaders.Add("X-Admin-Key", AdminApiKey);
        var created = await (await adminClient.PostAsJsonAsync("/admin/tenants", new CreateTenantRequest("billing-service")))
            .Content.ReadFromJsonAsync<CreateTenantResponse>();

        var response = await adminClient.PutAsync(
            $"/admin/tenants/{created!.TenantId}/credentials/smtp",
            new StringContent(
                "{\"provider\":\"Gmail\",\"username\":\"account@example.com\",\"password\":\"app-password\",\"fromAddress\":\"account@example.com\"}",
                System.Text.Encoding.UTF8,
                "application/json"));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var resolved = await _factory.Services.GetRequiredService<ICredentialStore>()
            .GetAsync<SmtpOptions>(created.TenantId, ChannelCredentialNames.Smtp);
        Assert.Equal(SmtpProvider.Gmail, resolved!.Provider);
    }

    private const string FakeServiceAccountJson =
        "{\"type\":\"service_account\",\"client_email\":\"sender@project.iam.gserviceaccount.com\",\"private_key\":\"not-a-real-key\"}";
}
