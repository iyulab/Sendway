using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sendway.Core;
using Sendway.Service.Endpoints;
using Xunit;

namespace Sendway.Service.Tests;

public class SendPushEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly FakePushSender _fakePushSender = new();

    public SendPushEndpointTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IPushSender>();
                services.AddSingleton<IPushSender>(_fakePushSender);
            });
        });
    }

    [Fact]
    public async Task Post_WithValidRequest_Returns200()
    {
        var client = _factory.CreateClient();
        var request = new SendPushRequest("device-token-123", "Distinct Title", "Distinct Body");

        var response = await client.PostAsJsonAsync("/messages/push", request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(_fakePushSender.LastMessage);
        Assert.Equal("device-token-123", _fakePushSender.LastMessage!.DeviceToken);
        Assert.Equal("Distinct Title", _fakePushSender.LastMessage!.Title);
        Assert.Equal("Distinct Body", _fakePushSender.LastMessage!.Body);
    }

    [Fact]
    public async Task Post_WithMissingDeviceToken_Returns400()
    {
        var client = _factory.CreateClient();
        var request = new SendPushRequest(null, "Title", "Body");

        var response = await client.PostAsJsonAsync("/messages/push", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
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
        var client = factory.CreateClient();
        var request = new SendPushRequest("expired-token", "Title", "Body");

        var response = await client.PostAsJsonAsync("/messages/push", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Post_WhenSenderThrows_Returns502()
    {
        var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IPushSender>();
                services.AddSingleton<IPushSender>(new FakePushSender(failureMode: FailureMode.Other));
            });
        });
        var client = factory.CreateClient();
        var request = new SendPushRequest("device-token-123", "Title", "Body");

        var response = await client.PostAsJsonAsync("/messages/push", request);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
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
