using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Sendway.Service.Tests;

// A send on a channel no credential lets the tenant use could never be delivered, so it is refused
// at acceptance instead of being stored and retried until it fails.
public class ChannelNotConfiguredTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private const string AdminApiKey = "test-admin-key";

    private readonly WebApplicationFactory<Program> _factory;
    private readonly TestIsolatedStorage _storage = new();

    public ChannelNotConfiguredTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((context, configuration) =>
            {
                _storage.Apply(context, configuration);
                configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Sendway:AdminApiKey"] = AdminApiKey });
            });
        });
    }

    public void Dispose() => _storage.Dispose();

    private HttpClient Admin()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Admin-Key", AdminApiKey);
        return client;
    }

    private HttpClient Tenant(string apiKey)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        return client;
    }

    private static Task<HttpResponseMessage> SendEmailAsync(HttpClient client, string? idempotencyKey = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/messages/email")
        {
            Content = JsonContent.Create(new { to = new[] { "user@example.com" }, subject = "s", body = "b" })
        };
        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        return client.SendAsync(request);
    }

    private static readonly object Smtp = new { host = "127.0.0.1", fromAddress = "sender@example.com" };

    private async Task<int> QueuedAsync()
    {
        var stats = await Admin().GetFromJsonAsync<JsonElement>("/admin/queues");
        return stats.GetProperty("messages").GetProperty("queued").GetInt32();
    }

    [Fact]
    public async Task ASendWithNoCredentialAtAll_IsRefusedWith409_AndNothingIsStored()
    {
        var (_, apiKey) = await TestTenantSeeder.SeedAsync(_factory, withChannelCredentials: false);

        var response = await SendEmailAsync(Tenant(apiKey));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("channel_not_configured", problem.GetProperty("code").GetString());
        Assert.Contains("email", problem.GetProperty("detail").GetString());
        Assert.Equal(0, await QueuedAsync());
    }

    [Fact]
    public async Task ASharedCredential_IsEnoughForATenantWithoutItsOwn()
    {
        var (_, apiKey) = await TestTenantSeeder.SeedAsync(_factory, withChannelCredentials: false);
        Assert.Equal(HttpStatusCode.NoContent, (await Admin().PutAsJsonAsync("/admin/credentials/smtp", Smtp)).StatusCode);

        var response = await SendEmailAsync(Tenant(apiKey));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    // The idempotency promise holds across a credential change: a retry of an accepted request gets the
    // original answer, while a new request is judged on the credentials as they are now.
    [Fact]
    public async Task ARetryOfAnAcceptedRequest_IsAnsweredAsBefore_EvenAfterTheCredentialIsGone()
    {
        var (tenantId, apiKey) = await TestTenantSeeder.SeedAsync(_factory, withChannelCredentials: false);
        var admin = Admin();
        await admin.PutAsJsonAsync($"/admin/tenants/{tenantId}/credentials/smtp", Smtp);
        var client = Tenant(apiKey);

        var first = await SendEmailAsync(client, "key-1");
        var firstId = (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        await admin.DeleteAsync($"/admin/tenants/{tenantId}/credentials/smtp");
        var retry = await SendEmailAsync(client, "key-1");
        var another = await SendEmailAsync(client, "key-2");

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);
        Assert.Equal(firstId, (await retry.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.Conflict, another.StatusCode);
    }
}
