using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sendway.Core;
using Sendway.Service.Endpoints;
using Sendway.Service.Endpoints.Admin;

namespace Sendway.Service.Tests;

// A sandbox tenant's messages go through the real path — acceptance, queue, status, webhook — but the
// simulator answers instead of a provider, and the destination picks the outcome.
public class SandboxTenantTests : IClassFixture<WebApplicationFactory<Program>>, IAsyncLifetime
{
    private const string AdminApiKey = "test-admin-key";

    private readonly WebApplicationFactory<Program> _factory;
    private readonly CountingEmailSender _emailSender = new();
    private readonly TestIsolatedStorage _storage = new();
    private CreateTenantResponse _sandbox = null!;

    public SandboxTenantTests(WebApplicationFactory<Program> factory)
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
                services.AddSingleton<IEmailSender>(_emailSender);
            });
        });
    }

    public async Task InitializeAsync()
    {
        var response = await Admin().PostAsJsonAsync("/admin/tenants", new CreateTenantRequest("sandbox-app", Sandbox: true));
        _sandbox = (await response.Content.ReadFromJsonAsync<CreateTenantResponse>())!;
    }

    public Task DisposeAsync()
    {
        _storage.Dispose();
        return Task.CompletedTask;
    }

    private HttpClient Admin()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Admin-Key", AdminApiKey);
        return client;
    }

    private HttpClient Caller()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", _sandbox.ApiKey);
        return client;
    }

    private async Task<MessageStatusResponse> SendAndDeliverAsync(string path, object request)
    {
        var client = Caller();
        var response = await client.PostAsJsonAsync(path, request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var accepted = (await response.Content.ReadFromJsonAsync<SendMessageResponse>())!;
        await TestDelivery.DeliverPendingAsync(_factory);
        return (await client.GetFromJsonAsync<MessageStatusResponse>($"/messages/{accepted.Id}"))!;
    }

    [Fact]
    public void ASandboxTenantsKey_SaysItIsATestKey()
    {
        Assert.True(_sandbox.Sandbox);
        Assert.StartsWith("sw_test_", _sandbox.ApiKey);
    }

    [Fact]
    public async Task AMessage_IsSentWithoutACredentialAndWithoutReachingAProvider()
    {
        var status = await SendAndDeliverAsync("/messages/email", new SendEmailRequest(["user@example.com"], "Subject", "Body"));

        Assert.Equal("Sent", status.Status);
        Assert.Equal(0, _emailSender.Calls);
    }

    [Fact]
    public async Task TheDestinationPicksTheOutcome()
    {
        var partly = await SendAndDeliverAsync("/messages/email", new SendEmailRequest(["user@example.com", "refused@example.com"], "S", "B"));
        var rejected = await SendAndDeliverAsync("/messages/sms", new SendSmsRequest("+821000000002", "Body"));
        var exhausted = await SendAndDeliverAsync("/messages/push", new SendPushRequest("exhausted-token", "Title", "Body"));

        Assert.Equal("Sent", partly.Status);
        Assert.Equal("refused@example.com", Assert.Single(partly.RefusedRecipients!).Address);
        Assert.Equal(("Failed", "message_refused"), (rejected.Status, rejected.Reason));
        Assert.Equal(("Failed", "attempts_exhausted", 1), (exhausted.Status, exhausted.Reason, exhausted.Attempts));
    }

    [Fact]
    public async Task SimulatedFailures_StayOutOfTheOperatorsCounts()
    {
        await SendAndDeliverAsync("/messages/sms", new SendSmsRequest("+821000000001", "Body"));

        var queues = await Admin().GetFromJsonAsync<System.Text.Json.JsonElement>("/admin/queues");
        Assert.Equal(0, queues.GetProperty("messages").GetProperty("failedInLast24Hours").GetInt32());
    }

    [Fact]
    public async Task RotatingTheKey_KeepsItATestKey()
    {
        var response = await Admin().PostAsync($"/admin/tenants/{_sandbox.TenantId}/rotate-key", null);
        var rotated = (await response.Content.ReadFromJsonAsync<RotateTenantKeyResponse>())!;

        Assert.StartsWith("sw_test_", rotated.ApiKey);
    }

    private sealed class CountingEmailSender : IEmailSender
    {
        public int Calls { get; private set; }

        public Task<EmailDelivery> SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(EmailDelivery.ToEveryone);
        }
    }
}
