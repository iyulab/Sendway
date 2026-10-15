using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sendway.Core;
using Sendway.Service.Endpoints;
using Xunit;

namespace Sendway.Service.Tests;

public class UsageEndpointTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private const string AdminApiKey = "test-admin-key";

    private readonly WebApplicationFactory<Program> _factory;
    private readonly TestIsolatedStorage _storage = new();

    public UsageEndpointTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((context, configuration) =>
            {
                _storage.Apply(context, configuration);
                configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Sendway:AdminApiKey"] = AdminApiKey });
            });
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IEmailSender>();
                services.AddSingleton<IEmailSender, NoopEmailSender>();
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

    [Fact]
    public async Task Get_ShowsWhichTenantsSend_AndHowTheirMessagesEnded()
    {
        var (sender, apiKey) = await TestTenantSeeder.SeedAsync(_factory, "sender");
        var (idle, _) = await TestTenantSeeder.SeedAsync(_factory, "idle");
        var tenant = _factory.CreateClient();
        tenant.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        await tenant.PostAsJsonAsync("/messages/email", new SendEmailRequest(["user@example.com"], "Subject", "Body"));
        await tenant.PostAsJsonAsync("/messages/email", new SendEmailRequest(["user@example.com"], "Subject", "Body"));
        await TestDelivery.DeliverPendingAsync(_factory);

        var usage = await Admin().GetFromJsonAsync<JsonElement>("/admin/usage");

        var tenants = usage.GetProperty("tenants").EnumerateArray().ToList();
        var s = tenants.Single(t => t.GetProperty("tenantId").GetGuid() == sender);
        Assert.Equal("sender", s.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.String, s.GetProperty("lastSentAt").ValueKind);
        Assert.Equal(2, s.GetProperty("channels").GetProperty("email").GetProperty("sent").GetInt32());
        Assert.Equal(0, s.GetProperty("channels").GetProperty("email").GetProperty("failed").GetInt32());

        var i = tenants.Single(t => t.GetProperty("tenantId").GetGuid() == idle);
        Assert.Equal(JsonValueKind.Null, i.GetProperty("lastSentAt").ValueKind);
        Assert.Empty(i.GetProperty("channels").EnumerateObject());

        // Counts only: nothing about where or what was sent.
        Assert.DoesNotContain("user@example.com", usage.GetRawText());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(367)]
    public async Task Get_WithDaysOutOfRange_Returns400(int days)
    {
        var response = await Admin().GetAsync($"/admin/usage?days={days}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Get_WithoutTheAdminKey_Returns401()
    {
        var response = await _factory.CreateClient().GetAsync("/admin/usage");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private sealed class NoopEmailSender : IEmailSender
    {
        public Task<EmailDelivery> SendAsync(EmailMessage message, CancellationToken cancellationToken = default) => Task.FromResult(EmailDelivery.ToEveryone);
    }
}
