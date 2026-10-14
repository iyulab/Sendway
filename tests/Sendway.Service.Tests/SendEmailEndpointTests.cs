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
            builder.UseEnvironment("Testing");
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
        var request = new SendEmailRequest(["user@example.com"], "Subject", "Body");

        var response = await client.PostAsJsonAsync("/messages/email", request);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<SendMessageResponse>();
        Assert.NotNull(body);
        Assert.NotEqual(Guid.Empty, body!.Id);
        Assert.Equal("Queued", body.Status);
        Assert.Equal($"/messages/{body.Id}", response.Headers.Location?.OriginalString);
        Assert.Null(_fakeEmailSender.LastMessage);

        var status = await GetStatusAsync(client, body.Id);
        Assert.Equal("Queued", status.Status);
        Assert.Equal(0, status.Attempts);
        Assert.Null(status.CompletedAt);
    }

    [Fact]
    public async Task Post_WithValidRequest_IsDeliveredAndReportedAsSent()
    {
        var client = CreateAuthenticatedClient();
        var request = new SendEmailRequest(["user@example.com"], "Distinct Subject", "Distinct Body");

        var response = await client.PostAsJsonAsync("/messages/email", request);
        var body = await response.Content.ReadFromJsonAsync<SendMessageResponse>();
        await TestDelivery.DeliverPendingAsync(_factory);

        Assert.NotNull(_fakeEmailSender.LastMessage);
        Assert.Equal(["user@example.com"], _fakeEmailSender.LastMessage!.To);
        Assert.Empty(_fakeEmailSender.LastMessage!.Cc);
        Assert.Empty(_fakeEmailSender.LastMessage!.Bcc);
        Assert.Equal("Distinct Subject", _fakeEmailSender.LastMessage!.Subject);
        Assert.Equal("Distinct Body", _fakeEmailSender.LastMessage!.Body);

        var status = await GetStatusAsync(client, body!.Id);
        Assert.Equal("email", status.Channel);
        Assert.Equal("user@example.com", status.Recipient);
        Assert.Equal("Sent", status.Status);
        Assert.Null(status.Error);
        Assert.Equal(1, status.Attempts);
        Assert.NotNull(status.CompletedAt);
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
        await TestDelivery.DeliverPendingAsync(_factory);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
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
        await TestDelivery.DeliverPendingAsync(_factory);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("<p>Body</p>", _fakeEmailSender.LastMessage!.HtmlBody);
    }

    [Fact]
    public async Task Post_WithoutHtmlBody_HtmlBodyIsNull()
    {
        var client = CreateAuthenticatedClient();
        var request = new SendEmailRequest(["user@example.com"], "Subject", "Body");

        var response = await client.PostAsJsonAsync("/messages/email", request);
        await TestDelivery.DeliverPendingAsync(_factory);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Null(_fakeEmailSender.LastMessage!.HtmlBody);
    }

    [Fact]
    public async Task Post_WithBlankHtmlBody_TreatsAsAbsent()
    {
        var client = CreateAuthenticatedClient();
        var request = new SendEmailRequest(["user@example.com"], "Subject", "Body", HtmlBody: "   ");

        var response = await client.PostAsJsonAsync("/messages/email", request);
        await TestDelivery.DeliverPendingAsync(_factory);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Null(_fakeEmailSender.LastMessage!.HtmlBody);
    }

    [Fact]
    public async Task Post_WithOversizedHtmlBody_Returns400()
    {
        var client = CreateAuthenticatedClient();
        var request = new SendEmailRequest(["user@example.com"], "Subject", "Body", HtmlBody: new string('h', 1_000_001));

        var response = await client.PostAsJsonAsync("/messages/email", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(await TestDeliveryHadWorkAsync());
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

        var status = await GetStatusAsync(client, sendBody!.Id);

        Assert.Equal("to1@example.com, to2@example.com; cc: cc1@example.com; bcc: bcc1@example.com", status.Recipient);
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
        Assert.False(await TestDeliveryHadWorkAsync());
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
        Assert.False(await TestDeliveryHadWorkAsync());
    }

    [Fact]
    public async Task Post_WithOversizedSubject_Returns400()
    {
        var client = CreateAuthenticatedClient();
        var request = new SendEmailRequest(["user@example.com"], new string('s', 201), "Body");

        var response = await client.PostAsJsonAsync("/messages/email", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(await TestDeliveryHadWorkAsync());
    }

    [Fact]
    public async Task Post_WithOversizedBody_Returns400()
    {
        var client = CreateAuthenticatedClient();
        var request = new SendEmailRequest(["user@example.com"], "Subject", new string('b', 1_000_001));

        var response = await client.PostAsJsonAsync("/messages/email", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(await TestDeliveryHadWorkAsync());
    }

    [Theory]
    [InlineData("not-an-email-address", null, null)]
    [InlineData("user@example.com", "not-an-email-address", null)]
    [InlineData("user@example.com", null, "not-an-email-address")]
    public async Task Post_WithMalformedEmailAddress_IsRefusedInsteadOfAccepted(string to, string? cc, string? bcc)
    {
        var client = CreateAuthenticatedClient();
        var request = new SendEmailRequest(
            [to],
            "Subject",
            "Body",
            Cc: cc is null ? null : [cc],
            Bcc: bcc is null ? null : [bcc]);

        var response = await client.PostAsJsonAsync("/messages/email", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ProblemResponse>();
        Assert.Equal("invalid_recipient", body!.Code);
        Assert.Contains("not-an-email-address", body.Detail);
        Assert.False(await TestDeliveryHadWorkAsync());
    }

    [Fact]
    public async Task Post_WhenTheProviderRejectsTheRecipient_IsAcceptedThenReportedAsFailed()
    {
        _fakeEmailSender.FailWith(new InvalidRecipientException("simulated rejected mailbox"));
        var client = CreateAuthenticatedClient();
        var request = new SendEmailRequest(["user@example.com"], "Subject", "Body");

        var response = await client.PostAsJsonAsync("/messages/email", request);
        var body = await response.Content.ReadFromJsonAsync<SendMessageResponse>();
        await TestDelivery.DeliverPendingAsync(_factory);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var status = await GetStatusAsync(client, body!.Id);
        Assert.Equal("Failed", status.Status);
        Assert.Equal("simulated rejected mailbox", status.Error);
        Assert.NotNull(status.CompletedAt);
    }

    [Fact]
    public async Task Post_WhenAnAttemptFailsTransiently_StaysQueuedWithTheErrorVisible()
    {
        _fakeEmailSender.FailWith(new InvalidOperationException("simulated SMTP failure"));
        var client = CreateAuthenticatedClient();
        var request = new SendEmailRequest(["user@example.com"], "Subject", "Body");

        var response = await client.PostAsJsonAsync("/messages/email", request);
        var body = await response.Content.ReadFromJsonAsync<SendMessageResponse>();
        await TestDelivery.DeliverPendingAsync(_factory);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var status = await GetStatusAsync(client, body!.Id);
        Assert.Equal("Queued", status.Status);
        Assert.Equal("simulated SMTP failure", status.Error);
        Assert.Equal(1, status.Attempts);
        Assert.Null(status.CompletedAt);
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

    // True if a refused request nevertheless left something for the delivery worker to pick up.
    private Task<bool> TestDeliveryHadWorkAsync() =>
        _factory.Services.GetRequiredService<MessageDeliveryProcessor>().ProcessNextAsync();


    private sealed class FakeEmailSender : IEmailSender
    {
        private Exception? _failure;

        public EmailMessage? LastMessage { get; private set; }

        public void FailWith(Exception failure) => _failure = failure;

        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
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
