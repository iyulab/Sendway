using System.Net;
using System.Net.Http.Json;
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

    private HttpClient CreateAuthenticatedClient(WebApplicationFactory<Program>? factory = null)
    {
        var client = (factory ?? _factory).CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", _apiKey);
        return client;
    }

    [Fact]
    public async Task Post_WithValidRequest_Returns200()
    {
        var client = CreateAuthenticatedClient();
        var request = new SendPushRequest("device-token-123", "Distinct Title", "Distinct Body");

        var response = await client.PostAsJsonAsync("/messages/push", request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(_fakePushSender.LastMessage);
        Assert.Equal("device-token-123", _fakePushSender.LastMessage!.DeviceToken);
        Assert.Equal("Distinct Title", _fakePushSender.LastMessage!.Title);
        Assert.Equal("Distinct Body", _fakePushSender.LastMessage!.Body);

        var body = await response.Content.ReadFromJsonAsync<SendMessageResponse>();
        Assert.NotNull(body);
        Assert.NotEqual(Guid.Empty, body!.Id);

        var statusResponse = await client.GetAsync($"/messages/{body.Id}");
        var status = await statusResponse.Content.ReadFromJsonAsync<MessageStatusResponse>();
        Assert.Equal("push", status!.Channel);
        Assert.Equal("device-token-123", status.Recipient);
        Assert.Equal("Sent", status.Status);
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

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(_fakePushSender.LastMessage);
    }

    [Fact]
    public async Task Post_WithOversizedBody_Returns400()
    {
        var client = CreateAuthenticatedClient();
        var request = new SendPushRequest("device-token-123", "Title", new string('b', 4001));

        var response = await client.PostAsJsonAsync("/messages/push", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(_fakePushSender.LastMessage);
    }

    [Fact]
    public async Task Post_WhenTokenInvalid_Returns400()
    {
        var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IPushSender>();
                services.AddSingleton<IPushSender>(new FakePushSender(failureMode: FailureMode.InvalidRecipient));
            });
        });
        var client = CreateAuthenticatedClient(factory);
        var request = new SendPushRequest("expired-token", "Title", "Body");

        var response = await client.PostAsJsonAsync("/messages/push", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Post_WhenTokenInvalid_RecordsFailedStatusQueryableById()
    {
        var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IPushSender>();
                services.AddSingleton<IPushSender>(new FakePushSender(failureMode: FailureMode.InvalidRecipient));
            });
        });
        var client = CreateAuthenticatedClient(factory);
        var request = new SendPushRequest("expired-token", "Title", "Body");

        var response = await client.PostAsJsonAsync("/messages/push", request);
        var body = await response.Content.ReadFromJsonAsync<SendFailureResponse>();

        Assert.NotNull(body);
        Assert.NotEqual(Guid.Empty, body!.MessageId);

        var statusResponse = await client.GetAsync($"/messages/{body.MessageId}");
        var status = await statusResponse.Content.ReadFromJsonAsync<MessageStatusResponse>();

        Assert.Equal("Failed", status!.Status);
        Assert.NotNull(status.Error);
    }

    private sealed record SendFailureResponse(string? Error, Guid MessageId);

    [Fact]
    public async Task Post_WhenSenderThrows_Returns500()
    {
        var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IPushSender>();
                services.AddSingleton<IPushSender>(new FakePushSender(failureMode: FailureMode.Other));
            });
        });
        var client = CreateAuthenticatedClient(factory);
        var request = new SendPushRequest("device-token-123", "Title", "Body");

        var response = await client.PostAsJsonAsync("/messages/push", request);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    private enum FailureMode
    {
        None,
        InvalidRecipient,
        Other
    }

    private sealed class FakePushSender : IPushSender
    {
        private readonly FailureMode _failureMode;

        public PushMessage? LastMessage { get; private set; }

        public FakePushSender(FailureMode failureMode = FailureMode.None)
        {
            _failureMode = failureMode;
        }

        public Task SendAsync(PushMessage message, CancellationToken cancellationToken = default)
        {
            switch (_failureMode)
            {
                case FailureMode.InvalidRecipient:
                    throw new InvalidRecipientException("simulated invalid device token");
                case FailureMode.Other:
                    throw new InvalidOperationException("simulated FCM failure");
                default:
                    LastMessage = message;
                    return Task.CompletedTask;
            }
        }
    }
}
