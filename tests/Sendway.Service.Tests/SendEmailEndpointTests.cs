using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sendway.Core;
using Sendway.Service.Endpoints;
using Xunit;

namespace Sendway.Service.Tests;

public class SendEmailEndpointTests : IClassFixture<WebApplicationFactory<Program>>, IAsyncLifetime
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly FakeEmailSender _fakeEmailSender = new();
    private readonly TestIsolatedStorage _storage = new();
    private string _apiKey = null!;

    public SendEmailEndpointTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration(_storage.Apply);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IEmailSender>();
                services.AddSingleton<IEmailSender>(_fakeEmailSender);
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
        var request = new SendEmailRequest("user@example.com", "Distinct Subject", "Distinct Body");

        var response = await client.PostAsJsonAsync("/messages/email", request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(_fakeEmailSender.LastMessage);
        Assert.Equal("user@example.com", _fakeEmailSender.LastMessage!.To);
        Assert.Equal("Distinct Subject", _fakeEmailSender.LastMessage!.Subject);
        Assert.Equal("Distinct Body", _fakeEmailSender.LastMessage!.Body);

        var body = await response.Content.ReadFromJsonAsync<SendMessageResponse>();
        Assert.NotNull(body);
        Assert.NotEqual(Guid.Empty, body!.Id);
    }

    [Fact]
    public async Task Post_WithoutApiKey_Returns401()
    {
        var client = _factory.CreateClient();
        var request = new SendEmailRequest("user@example.com", "Subject", "Body");

        var response = await client.PostAsJsonAsync("/messages/email", request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Post_WithInvalidApiKey_Returns401()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "sw_not-a-real-key");
        var request = new SendEmailRequest("user@example.com", "Subject", "Body");

        var response = await client.PostAsJsonAsync("/messages/email", request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Post_WithValidRequest_StatusIsQueryableAfterward()
    {
        var client = CreateAuthenticatedClient();
        var request = new SendEmailRequest("user@example.com", "Subject", "Body");

        var sendResponse = await client.PostAsJsonAsync("/messages/email", request);
        var sendBody = await sendResponse.Content.ReadFromJsonAsync<SendMessageResponse>();

        var statusResponse = await client.GetAsync($"/messages/{sendBody!.Id}");
        var status = await statusResponse.Content.ReadFromJsonAsync<MessageStatusResponse>();

        Assert.Equal(HttpStatusCode.OK, statusResponse.StatusCode);
        Assert.Equal("email", status!.Channel);
        Assert.Equal("user@example.com", status.Recipient);
        Assert.Equal("Sent", status.Status);
        Assert.Null(status.Error);
    }

    [Fact]
    public async Task Post_WithMissingTo_Returns400()
    {
        var client = CreateAuthenticatedClient();
        var request = new SendEmailRequest(null, "Subject", "Body");

        var response = await client.PostAsJsonAsync("/messages/email", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Post_WithMalformedEmailAddress_RecordsFailedStatusQueryableById()
    {
        var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IEmailSender>();
                services.AddSingleton<IEmailSender>(new FakeEmailSender(failureMode: FailureMode.InvalidRecipient));
            });
        });
        var client = CreateAuthenticatedClient(factory);
        var request = new SendEmailRequest("not-an-email-address", "Subject", "Body");

        var response = await client.PostAsJsonAsync("/messages/email", request);
        var body = await response.Content.ReadFromJsonAsync<SendMessageFailureResponse>();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotNull(body);
        Assert.NotEqual(Guid.Empty, body!.MessageId);

        var statusResponse = await client.GetAsync($"/messages/{body.MessageId}");
        var status = await statusResponse.Content.ReadFromJsonAsync<MessageStatusResponse>();

        Assert.Equal("Failed", status!.Status);
        Assert.NotNull(status.Error);
    }

    [Fact]
    public async Task Post_WhenSenderThrows_Returns502()
    {
        var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IEmailSender>();
                services.AddSingleton<IEmailSender>(new FakeEmailSender(failureMode: FailureMode.Other));
            });
        });
        var client = CreateAuthenticatedClient(factory);
        var request = new SendEmailRequest("user@example.com", "Subject", "Body");

        var response = await client.PostAsJsonAsync("/messages/email", request);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    [Fact]
    public async Task Get_UnknownId_Returns404()
    {
        var client = CreateAuthenticatedClient();

        var response = await client.GetAsync($"/messages/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Get_MessageBelongingToAnotherTenant_Returns404()
    {
        var client = CreateAuthenticatedClient();
        var sendResponse = await client.PostAsJsonAsync("/messages/email", new SendEmailRequest("user@example.com", "Subject", "Body"));
        var sendBody = await sendResponse.Content.ReadFromJsonAsync<SendMessageResponse>();

        var (_, otherApiKey) = await TestTenantSeeder.SeedAsync(_factory, "other-tenant");
        var otherClient = _factory.CreateClient();
        otherClient.DefaultRequestHeaders.Add("X-Api-Key", otherApiKey);

        var statusResponse = await otherClient.GetAsync($"/messages/{sendBody!.Id}");

        Assert.Equal(HttpStatusCode.NotFound, statusResponse.StatusCode);
    }

    private enum FailureMode
    {
        None,
        InvalidRecipient,
        Other
    }

    private sealed class FakeEmailSender : IEmailSender
    {
        private readonly FailureMode _failureMode;

        public EmailMessage? LastMessage { get; private set; }

        public FakeEmailSender(FailureMode failureMode = FailureMode.None)
        {
            _failureMode = failureMode;
        }

        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
        {
            switch (_failureMode)
            {
                case FailureMode.InvalidRecipient:
                    throw new InvalidRecipientException("simulated malformed email address");
                case FailureMode.Other:
                    throw new InvalidOperationException("simulated SMTP failure");
                default:
                    LastMessage = message;
                    return Task.CompletedTask;
            }
        }
    }
}
