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
        var request = new SendEmailRequest(["user@example.com"], "Distinct Subject", "Distinct Body");

        var response = await client.PostAsJsonAsync("/messages/email", request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(_fakeEmailSender.LastMessage);
        Assert.Equal(["user@example.com"], _fakeEmailSender.LastMessage!.To);
        Assert.Empty(_fakeEmailSender.LastMessage!.Cc);
        Assert.Empty(_fakeEmailSender.LastMessage!.Bcc);
        Assert.Equal("Distinct Subject", _fakeEmailSender.LastMessage!.Subject);
        Assert.Equal("Distinct Body", _fakeEmailSender.LastMessage!.Body);

        var body = await response.Content.ReadFromJsonAsync<SendMessageResponse>();
        Assert.NotNull(body);
        Assert.NotEqual(Guid.Empty, body!.Id);
    }

    [Fact]
    public async Task Post_WithMultipleToCcBcc_SendsAllRecipients()
    {
        var client = CreateAuthenticatedClient();
        var request = new SendEmailRequest(
            To: ["to1@example.com", "to2@example.com"],
            Subject: "Subject",
            Body: "Body",
            Cc: ["cc1@example.com"],
            Bcc: ["bcc1@example.com", "bcc2@example.com"]);

        var response = await client.PostAsJsonAsync("/messages/email", request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(_fakeEmailSender.LastMessage);
        Assert.Equal(["to1@example.com", "to2@example.com"], _fakeEmailSender.LastMessage!.To);
        Assert.Equal(["cc1@example.com"], _fakeEmailSender.LastMessage!.Cc);
        Assert.Equal(["bcc1@example.com", "bcc2@example.com"], _fakeEmailSender.LastMessage!.Bcc);
    }

    [Fact]
    public async Task Post_WithHtmlBody_PassesHtmlBodyToSender()
    {
        var client = CreateAuthenticatedClient();
        var request = new SendEmailRequest(["user@example.com"], "Subject", "Body", HtmlBody: "<p>Body</p>");

        var response = await client.PostAsJsonAsync("/messages/email", request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("<p>Body</p>", _fakeEmailSender.LastMessage!.HtmlBody);
    }

    [Fact]
    public async Task Post_WithoutHtmlBody_HtmlBodyIsNull()
    {
        var client = CreateAuthenticatedClient();
        var request = new SendEmailRequest(["user@example.com"], "Subject", "Body");

        var response = await client.PostAsJsonAsync("/messages/email", request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(_fakeEmailSender.LastMessage!.HtmlBody);
    }

    [Fact]
    public async Task Post_WithBlankHtmlBody_TreatsAsAbsent()
    {
        var client = CreateAuthenticatedClient();
        var request = new SendEmailRequest(["user@example.com"], "Subject", "Body", HtmlBody: "   ");

        var response = await client.PostAsJsonAsync("/messages/email", request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(_fakeEmailSender.LastMessage!.HtmlBody);
    }

    [Fact]
    public async Task Post_WithOversizedHtmlBody_Returns400()
    {
        var client = CreateAuthenticatedClient();
        var request = new SendEmailRequest(["user@example.com"], "Subject", "Body", HtmlBody: new string('h', 1_000_001));

        var response = await client.PostAsJsonAsync("/messages/email", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(_fakeEmailSender.LastMessage);
    }

    [Fact]
    public async Task Post_WithoutApiKey_Returns401()
    {
        var client = _factory.CreateClient();
        var request = new SendEmailRequest(["user@example.com"], "Subject", "Body");

        var response = await client.PostAsJsonAsync("/messages/email", request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Post_WithInvalidApiKey_Returns401()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "sw_not-a-real-key");
        var request = new SendEmailRequest(["user@example.com"], "Subject", "Body");

        var response = await client.PostAsJsonAsync("/messages/email", request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Post_WithValidRequest_StatusIsQueryableAfterward()
    {
        var client = CreateAuthenticatedClient();
        var request = new SendEmailRequest(["user@example.com"], "Subject", "Body");

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
    public async Task Post_WithMultipleRecipients_StatusRecipientSummarizesToCcBcc()
    {
        var client = CreateAuthenticatedClient();
        var request = new SendEmailRequest(
            To: ["to1@example.com", "to2@example.com"],
            Subject: "Subject",
            Body: "Body",
            Cc: ["cc1@example.com"],
            Bcc: ["bcc1@example.com"]);

        var sendResponse = await client.PostAsJsonAsync("/messages/email", request);
        var sendBody = await sendResponse.Content.ReadFromJsonAsync<SendMessageResponse>();

        var statusResponse = await client.GetAsync($"/messages/{sendBody!.Id}");
        var status = await statusResponse.Content.ReadFromJsonAsync<MessageStatusResponse>();

        Assert.Equal("to1@example.com, to2@example.com; cc: cc1@example.com; bcc: bcc1@example.com", status!.Recipient);
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
    public async Task Post_WithEmptyToList_Returns400()
    {
        var client = CreateAuthenticatedClient();
        var request = new SendEmailRequest([], "Subject", "Body");

        var response = await client.PostAsJsonAsync("/messages/email", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Post_WithBlankAddressInCc_Returns400()
    {
        var client = CreateAuthenticatedClient();
        var request = new SendEmailRequest(["user@example.com"], "Subject", "Body", Cc: [" "]);

        var response = await client.PostAsJsonAsync("/messages/email", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(_fakeEmailSender.LastMessage);
    }

    [Fact]
    public async Task Post_WithTooManyRecipients_Returns400()
    {
        var client = CreateAuthenticatedClient();
        var request = new SendEmailRequest(
            To: Enumerable.Range(0, 1001).Select(i => $"to{i}@example.com").ToList(),
            Subject: "Subject",
            Body: "Body");

        var response = await client.PostAsJsonAsync("/messages/email", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(_fakeEmailSender.LastMessage);
    }

    [Fact]
    public async Task Post_WithOversizedSubject_Returns400()
    {
        var client = CreateAuthenticatedClient();
        var request = new SendEmailRequest(["user@example.com"], new string('s', 201), "Body");

        var response = await client.PostAsJsonAsync("/messages/email", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(_fakeEmailSender.LastMessage);
    }

    [Fact]
    public async Task Post_WithOversizedBody_Returns400()
    {
        var client = CreateAuthenticatedClient();
        var request = new SendEmailRequest(["user@example.com"], "Subject", new string('b', 1_000_001));

        var response = await client.PostAsJsonAsync("/messages/email", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(_fakeEmailSender.LastMessage);
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
        var request = new SendEmailRequest(["not-an-email-address"], "Subject", "Body");

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
    public async Task Post_WhenSenderThrows_Returns500()
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
        var request = new SendEmailRequest(["user@example.com"], "Subject", "Body");

        var response = await client.PostAsJsonAsync("/messages/email", request);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
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
        var sendResponse = await client.PostAsJsonAsync("/messages/email", new SendEmailRequest(["user@example.com"], "Subject", "Body"));
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
