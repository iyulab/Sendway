using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sendway.Core;
using Sendway.Service.Endpoints.Admin;
using Xunit;

namespace Sendway.Service.Tests;

public class AdminAuditTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private const string AdminApiKey = "test-admin-key";

    private static readonly string ExpectedActor = "admin-key:" + ApiKeyGenerator.Hash(AdminApiKey)[..12].ToLowerInvariant();

    private readonly WebApplicationFactory<Program> _factory;
    private readonly TestIsolatedStorage _storage = new();

    public AdminAuditTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            // "Testing" loads no local appsettings.Development.json, so no shared credential comes
            // from configuration and the shared endpoints accept changes.
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

    private HttpClient CreateAdminClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Admin-Key", AdminApiKey);
        return client;
    }

    private static StringContent Json(string body) => new(body, System.Text.Encoding.UTF8, "application/json");

    private async Task<Guid> CreateTenantAsync(HttpClient client, string name = "billing-service")
    {
        var created = await (await client.PostAsJsonAsync("/admin/tenants", new CreateTenantRequest(name)))
            .Content.ReadFromJsonAsync<CreateTenantResponse>();
        return created!.TenantId;
    }

    private static async Task<List<AuditEventResponse>> ReadAuditAsync(HttpClient client, string query = "")
    {
        var response = await client.GetAsync("/admin/audit" + query);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<List<AuditEventResponse>>())!;
    }

    // A new endpoint that changes state but records nothing would leave a gap in the log nobody
    // notices; this fails as soon as one is mapped without declaring its action.
    [Fact]
    public void EveryAdminEndpointThatChangesState_DeclaresAnAuditAction()
    {
        var endpoints = _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.StartsWith("/admin", StringComparison.Ordinal) == true)
            .ToList();

        var changing = endpoints
            .Where(e => e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Any(m => m is "POST" or "PUT" or "DELETE") == true)
            .ToList();

        Assert.NotEmpty(changing);
        Assert.All(changing, e => Assert.True(
            e.Metadata.GetMetadata<AuditAction>() is not null,
            $"{e.DisplayName} changes state but declares no audit action."));
    }

    [Fact]
    public async Task EachChange_IsRecorded_NewestFirst_WithActorAndSubject()
    {
        var client = CreateAdminClient();
        var tenantId = await CreateTenantAsync(client);

        await client.PostAsync($"/admin/tenants/{tenantId}/deactivate", null);
        await client.PostAsync($"/admin/tenants/{tenantId}/reactivate", null);
        await client.PostAsync($"/admin/tenants/{tenantId}/rotate-key", null);
        await client.PutAsync($"/admin/tenants/{tenantId}/credentials/fcm", Json("{\"credentialsJson\":\"{\\\"client_email\\\":\\\"a@b.c\\\",\\\"private_key\\\":\\\"k\\\"}\"}"));
        await client.DeleteAsync($"/admin/tenants/{tenantId}/credentials/fcm");
        await client.PutAsync($"/admin/tenants/{tenantId}/webhook", Json("{\"url\":\"https://receiver.example.com/hook\"}"));
        await client.DeleteAsync($"/admin/tenants/{tenantId}/webhook");

        var events = await ReadAuditAsync(client, $"?tenantId={tenantId}");

        Assert.Equal(
            ["webhook.deleted", "webhook.set", "credential.deleted", "credential.set", "tenant.key_rotated", "tenant.reactivated", "tenant.deactivated", "tenant.created"],
            events.Select(e => e.Action));
        Assert.All(events, e =>
        {
            Assert.Equal(tenantId, e.TenantId);
            Assert.Equal(ExpectedActor, e.Actor);
        });
        Assert.Equal("billing-service", events.Single(e => e.Action == "tenant.created").Subject);
        Assert.Equal("fcm", events.Single(e => e.Action == "credential.set").Subject);
        Assert.Equal("https://receiver.example.com/hook", events.Single(e => e.Action == "webhook.set").Subject);
    }

    [Fact]
    public async Task SharedCredentialChange_IsRecordedWithoutTenant()
    {
        var client = CreateAdminClient();

        var put = await client.PutAsync("/admin/credentials/solapi", Json("{\"apiKey\":\"k\",\"apiSecret\":\"s\",\"from\":\"0212345678\"}"));
        var delete = await client.DeleteAsync("/admin/credentials/solapi");

        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        var events = await ReadAuditAsync(client);
        Assert.Equal(["credential.deleted", "credential.set"], events.Select(e => e.Action));
        Assert.All(events, e =>
        {
            Assert.Null(e.TenantId);
            Assert.Equal("solapi", e.Subject);
        });
    }

    [Fact]
    public async Task RefusedOrUnmatchedChange_IsNotRecorded()
    {
        var client = CreateAdminClient();
        var tenantId = await CreateTenantAsync(client);

        await client.PutAsync($"/admin/tenants/{tenantId}/credentials/smtp", Json("{}"));
        await client.PostAsync($"/admin/tenants/{Guid.NewGuid()}/deactivate", null);
        await client.DeleteAsync($"/admin/tenants/{tenantId}/webhook");

        var events = await ReadAuditAsync(client);
        Assert.Equal(["tenant.created"], events.Select(e => e.Action));
    }

    [Fact]
    public async Task Log_HoldsNoSecret()
    {
        var client = CreateAdminClient();
        var created = await (await client.PostAsJsonAsync("/admin/tenants", new CreateTenantRequest("billing-service")))
            .Content.ReadFromJsonAsync<CreateTenantResponse>();
        var rotated = await (await client.PostAsync($"/admin/tenants/{created!.TenantId}/rotate-key", null))
            .Content.ReadFromJsonAsync<RotateTenantKeyResponse>();
        await client.PutAsync($"/admin/tenants/{created.TenantId}/credentials/smtp",
            Json("{\"host\":\"smtp.example.com\",\"username\":\"u\",\"password\":\"credential-password\",\"fromAddress\":\"a@example.com\"}"));

        var raw = await client.GetStringAsync("/admin/audit");

        Assert.DoesNotContain(created.ApiKey, raw);
        Assert.DoesNotContain(rotated!.ApiKey, raw);
        Assert.DoesNotContain("credential-password", raw);
        Assert.DoesNotContain(AdminApiKey, raw);
    }

    [Fact]
    public async Task Paging_WithBefore_ContinuesWithoutGapsOrRepeats_EvenWithinOneInstant()
    {
        var auditLog = _factory.Services.GetRequiredService<IAuditLog>();
        var instant = DateTimeOffset.UtcNow;
        for (var i = 0; i < 5; i++)
        {
            await auditLog.AppendAsync(new AuditEvent
            {
                Id = Guid.NewGuid(),
                OccurredAt = instant,
                Actor = "test",
                Action = "test.event"
            });
        }

        var client = CreateAdminClient();
        var seen = new List<Guid>();
        var page = await ReadAuditAsync(client, "?limit=2");
        while (page.Count > 0)
        {
            seen.AddRange(page.Select(e => e.Id));
            page = await ReadAuditAsync(client, $"?limit=2&before={page[^1].Id}");
        }

        Assert.Equal(5, seen.Count);
        Assert.Equal(5, seen.Distinct().Count());
    }

    [Theory]
    [InlineData("?limit=0")]
    [InlineData("?limit=1001")]
    [InlineData("?before=00000000-0000-0000-0000-000000000001")]
    public async Task BadQuery_Returns400(string query)
    {
        var response = await CreateAdminClient().GetAsync("/admin/audit" + query);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Audit_WithoutAdminKey_Returns401()
    {
        var response = await _factory.CreateClient().GetAsync("/admin/audit");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
