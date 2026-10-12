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
using Sendway.Service.Endpoints.Admin;
using Xunit;

namespace Sendway.Service.Tests;

public class WebhookTests : IClassFixture<WebApplicationFactory<Program>>, IAsyncLifetime
{
    private const string AdminApiKey = "test-admin-key";

    private readonly WebApplicationFactory<Program> _factory;
    private readonly ScriptedEmailSender _emailSender = new();
    private readonly RecordingWebhookSender _webhookSender = new();
    private readonly TestIsolatedStorage _storage = new();
    private Guid _tenantId;
    private string _apiKey = null!;

    public WebhookTests(WebApplicationFactory<Program> factory)
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
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IEmailSender>();
                services.AddSingleton<IEmailSender>(_emailSender);
                services.RemoveAll<IWebhookSender>();
                services.AddSingleton<IWebhookSender>(_webhookSender);
            });
        });
    }

    public async Task InitializeAsync()
    {
        (_tenantId, _apiKey) = await TestTenantSeeder.SeedAsync(_factory);
    }

    public Task DisposeAsync()
    {
        _storage.Dispose();
        return Task.CompletedTask;
    }

    private HttpClient CreateAdminClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Admin-Key", AdminApiKey);
        return client;
    }

    private HttpClient CreateTenantClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", _apiKey);
        return client;
    }

    private Task<HttpResponseMessage> RegisterAsync(string url = "https://receiver.example.com/hooks") =>
        CreateAdminClient().PutAsJsonAsync($"/admin/tenants/{_tenantId}/webhook", new SetTenantWebhookRequest(url));

    private async Task<Guid> SendEmailAsync()
    {
        var response = await CreateTenantClient().PostAsJsonAsync(
            "/messages/email",
            new SendEmailRequest(["user@example.com"], "Subject", "Body"));
        return (await response.Content.ReadFromJsonAsync<SendMessageResponse>())!.Id;
    }

    [Fact]
    public async Task Put_RegistersTheEndpointAndReturnsItsSigningSecret()
    {
        var response = await RegisterAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<TenantWebhookRegistrationResponse>();
        Assert.Equal("https://receiver.example.com/hooks", body!.Url);
        Assert.StartsWith("whsec_", body.Secret);
    }

    [Fact]
    public async Task Get_ShowsTheEndpointWithoutItsSecret()
    {
        await RegisterAsync();

        var response = await CreateAdminClient().GetAsync($"/admin/tenants/{_tenantId}/webhook");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        Assert.Contains("https://receiver.example.com/hooks", json);
        Assert.DoesNotContain("whsec_", json);
    }

    [Fact]
    public async Task Get_WhenNothingIsRegistered_Returns404()
    {
        var response = await CreateAdminClient().GetAsync($"/admin/tenants/{_tenantId}/webhook");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Delete_RemovesTheEndpoint()
    {
        await RegisterAsync();
        var admin = CreateAdminClient();

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/admin/tenants/{_tenantId}/webhook")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/admin/tenants/{_tenantId}/webhook")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync($"/admin/tenants/{_tenantId}/webhook")).StatusCode);
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("/relative/path")]
    [InlineData("ftp://receiver.example.com/hooks")]
    [InlineData("")]
    public async Task Put_WithAnUnusableUrl_Returns400(string url)
    {
        var response = await RegisterAsync(url);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Put_ForAnUnknownTenant_Returns404()
    {
        var response = await CreateAdminClient().PutAsJsonAsync(
            $"/admin/tenants/{Guid.NewGuid()}/webhook",
            new SetTenantWebhookRequest("https://receiver.example.com/hooks"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Put_WithoutTheAdminKey_Returns401()
    {
        var response = await _factory.CreateClient().PutAsJsonAsync(
            $"/admin/tenants/{_tenantId}/webhook",
            new SetTenantWebhookRequest("https://receiver.example.com/hooks"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task SentMessage_IsAnnouncedToTheRegisteredEndpoint()
    {
        var registration = await (await RegisterAsync()).Content.ReadFromJsonAsync<TenantWebhookRegistrationResponse>();
        var messageId = await SendEmailAsync();

        await TestDelivery.DeliverPendingAsync(_factory);

        var call = Assert.Single(_webhookSender.Calls);
        Assert.Equal("https://receiver.example.com/hooks", call.Url.AbsoluteUri);
        Assert.Equal(registration!.Secret, call.Secret);

        using var body = JsonDocument.Parse(call.Body);
        Assert.Equal("message.sent", body.RootElement.GetProperty("type").GetString());
        Assert.Equal(messageId, body.RootElement.GetProperty("data").GetProperty("id").GetGuid());

        // The event's data is the same document the status endpoint serves for that message.
        var status = await CreateTenantClient().GetFromJsonAsync<JsonElement>($"/messages/{messageId}");
        Assert.Equal(status.GetRawText(), body.RootElement.GetProperty("data").GetRawText());
    }

    [Fact]
    public async Task FailedMessage_IsAnnouncedAsFailed()
    {
        await RegisterAsync();
        _emailSender.FailWith(new InvalidRecipientException("simulated rejected mailbox"));
        var messageId = await SendEmailAsync();

        await TestDelivery.DeliverPendingAsync(_factory);

        var call = Assert.Single(_webhookSender.Calls);
        using var body = JsonDocument.Parse(call.Body);
        Assert.Equal("message.failed", body.RootElement.GetProperty("type").GetString());
        Assert.Equal(messageId, body.RootElement.GetProperty("data").GetProperty("id").GetGuid());
        Assert.Equal("simulated rejected mailbox", body.RootElement.GetProperty("data").GetProperty("error").GetString());
    }

    [Fact]
    public async Task MessageStillWaitingToRetry_IsNotAnnounced()
    {
        await RegisterAsync();
        _emailSender.FailWith(new InvalidOperationException("simulated SMTP failure"));
        await SendEmailAsync();

        await TestDelivery.DeliverPendingAsync(_factory);

        Assert.Empty(_webhookSender.Calls);
    }

    [Fact]
    public async Task WithoutARegisteredEndpoint_NothingIsAnnounced()
    {
        await SendEmailAsync();

        await TestDelivery.DeliverPendingAsync(_factory);

        Assert.Empty(_webhookSender.Calls);
    }

    private sealed class ScriptedEmailSender : IEmailSender
    {
        private Exception? _failure;

        public void FailWith(Exception failure) => _failure = failure;

        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default) =>
            _failure is null ? Task.CompletedTask : Task.FromException(_failure);
    }

    private sealed record WebhookCall(Uri Url, string Secret, Guid EventId, string Body);

    private sealed class RecordingWebhookSender : IWebhookSender
    {
        public List<WebhookCall> Calls { get; } = [];

        public Task SendAsync(Uri url, string secret, Guid eventId, string body, CancellationToken cancellationToken = default)
        {
            Calls.Add(new WebhookCall(url, secret, eventId, body));
            return Task.CompletedTask;
        }
    }
}
