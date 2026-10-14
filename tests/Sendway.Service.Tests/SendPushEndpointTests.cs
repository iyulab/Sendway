using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sendway.Core;
using Sendway.Service.Endpoints;
using Xunit;

namespace Sendway.Service.Tests;

public class SendPushEndpointTests : IClassFixture<WebApplicationFactory<Program>>, IAsyncLifetime
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly FakePushSender _fakePushSender = new();
    private readonly TestIsolatedStorage _storage = new();
    private string _apiKey = null!;

    public SendPushEndpointTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration(_storage.Apply);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IPushSender>();
                services.AddSingleton<IPushSender>(_fakePushSender);
            });
        });
    }

    public async Task InitializeAsync()
    {
        (_, _apiKey) = await TestTenantSeeder.SeedAsync(_factory);
    }

    public Task DisposeAsync()
    {
        _storage.Dispose();
        return Task.CompletedTask;
    }

    private HttpClient CreateAuthenticatedClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", _apiKey);
        return client;
    }

    private static async Task<MessageStatusResponse> GetStatusAsync(HttpClient client, Guid id) =>
        (await client.GetFromJsonAsync<MessageStatusResponse>($"/messages/{id}"))!;

    [Fact]
    public async Task Post_WithValidRequest_AcceptsWithoutSendingYet()
    {
        var client = CreateAuthenticatedClient();
        var request = new SendPushRequest("device-token-123", "Title", "Body");

        var response = await client.PostAsJsonAsync("/messages/push", request);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<SendMessageResponse>();
        Assert.NotNull(body);
        Assert.NotEqual(Guid.Empty, body!.Id);
        Assert.Equal("Queued", body.Status);
        Assert.Equal($"/messages/{body.Id}", response.Headers.Location?.OriginalString);
        Assert.Null(_fakePushSender.LastMessage);
    }

    [Fact]
    public async Task Post_WithValidRequest_IsDeliveredAndReportedAsSent()
    {
        var client = CreateAuthenticatedClient();
        var request = new SendPushRequest("device-token-123", "Distinct Title", "Distinct Body");

        var response = await client.PostAsJsonAsync("/messages/push", request);
        var body = await response.Content.ReadFromJsonAsync<SendMessageResponse>();
        await TestDelivery.DeliverPendingAsync(_factory);

        Assert.NotNull(_fakePushSender.LastMessage);
        Assert.Equal("device-token-123", _fakePushSender.LastMessage!.DeviceToken);
        Assert.Equal("Distinct Title", _fakePushSender.LastMessage!.Title);
        Assert.Equal("Distinct Body", _fakePushSender.LastMessage!.Body);

        var status = await GetStatusAsync(client, body!.Id);
        Assert.Equal("push", status.Channel);
        Assert.Equal("device-token-123", status.Recipient);
        Assert.Equal("Sent", status.Status);
        Assert.Equal(1, status.Attempts);
    }

    [Fact]
    public async Task Post_WithoutApiKey_Returns401()
    {
        var client = _factory.CreateClient();
        var request = new SendPushRequest("device-token-123", "Title", "Body");

        var response = await client.PostAsJsonAsync("/messages/push", request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Post_WithMissingDeviceToken_Returns400()
    {
        var client = CreateAuthenticatedClient();
        var request = new SendPushRequest(null, "Title", "Body");

        var response = await client.PostAsJsonAsync("/messages/push", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Post_WithOversizedTitle_Returns400()
    {
        var client = CreateAuthenticatedClient();
        var request = new SendPushRequest("device-token-123", new string('t', 201), "Body");

        var response = await client.PostAsJsonAsync("/messages/push", request);
        await TestDelivery.DeliverPendingAsync(_factory);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(_fakePushSender.LastMessage);
    }

    [Fact]
    public async Task Post_WithOversizedBody_Returns400()
    {
        var client = CreateAuthenticatedClient();
        var request = new SendPushRequest("device-token-123", "Title", new string('b', 4001));

        var response = await client.PostAsJsonAsync("/messages/push", request);
        await TestDelivery.DeliverPendingAsync(_factory);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(_fakePushSender.LastMessage);
    }

    [Fact]
    public async Task Post_WhenTheProviderRejectsTheToken_IsAcceptedThenReportedAsFailed()
    {
        _fakePushSender.FailWith(new InvalidRecipientException("simulated invalid device token"));
        var client = CreateAuthenticatedClient();
        var request = new SendPushRequest("expired-token", "Title", "Body");

        var response = await client.PostAsJsonAsync("/messages/push", request);
        var body = await response.Content.ReadFromJsonAsync<SendMessageResponse>();
        await TestDelivery.DeliverPendingAsync(_factory);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var status = await GetStatusAsync(client, body!.Id);
        Assert.Equal("Failed", status.Status);
        Assert.Equal("simulated invalid device token", status.Error);
        Assert.Equal(1, status.Attempts);
    }

    [Fact]
    public async Task Post_WhenAnAttemptFailsTransiently_StaysQueuedWithTheErrorVisible()
    {
        _fakePushSender.FailWith(new InvalidOperationException("simulated FCM failure"));
        var client = CreateAuthenticatedClient();
        var request = new SendPushRequest("device-token-123", "Title", "Body");

        var response = await client.PostAsJsonAsync("/messages/push", request);
        var body = await response.Content.ReadFromJsonAsync<SendMessageResponse>();
        await TestDelivery.DeliverPendingAsync(_factory);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var status = await GetStatusAsync(client, body!.Id);
        Assert.Equal("Queued", status.Status);
        Assert.Equal("simulated FCM failure", status.Error);
    }

    private sealed class FakePushSender : IPushSender
    {
        private Exception? _failure;

        public PushMessage? LastMessage { get; private set; }

        public void FailWith(Exception failure) => _failure = failure;

        public Task SendAsync(PushMessage message, CancellationToken cancellationToken = default)
        {
            if (_failure is not null)
            {
                throw _failure;
            }

            LastMessage = message;
            return Task.CompletedTask;
        }
    }
}
