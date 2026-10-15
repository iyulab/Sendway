using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sendway.Core;
using Sendway.Service.Endpoints;
using Xunit;

namespace Sendway.Service.Tests;

public class SendSmsEndpointTests : IClassFixture<WebApplicationFactory<Program>>, IAsyncLifetime
{
    private const string AdminApiKey = "test-admin-key";

    private readonly WebApplicationFactory<Program> _factory;
    private readonly FakeSmsSender _fakeSmsSender = new();
    private readonly TestIsolatedStorage _storage = new();
    private Guid _tenantId;
    private string _apiKey = null!;

    public SendSmsEndpointTests(WebApplicationFactory<Program> factory)
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
                services.RemoveAll<ISmsSender>();
                services.AddSingleton<ISmsSender>(_fakeSmsSender);
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

    private HttpClient CreateAuthenticatedClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", _apiKey);
        return client;
    }

    private HttpClient CreateAdminClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Admin-Key", AdminApiKey);
        return client;
    }

    [Fact]
    public async Task Post_WithValidRequest_IsAcceptedThenDeliveredAndReportedAsSent()
    {
        var client = CreateAuthenticatedClient();

        var response = await client.PostAsJsonAsync("/messages/sms", new SendSmsRequest("+821012345678", "Your code is 123456"));
        var body = await response.Content.ReadFromJsonAsync<SendMessageResponse>();
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("Queued", body!.Status);
        Assert.Null(_fakeSmsSender.LastMessage);

        await TestDelivery.DeliverPendingAsync(_factory);

        Assert.Equal("+821012345678", _fakeSmsSender.LastMessage!.To.ToString());
        Assert.Equal("Your code is 123456", _fakeSmsSender.LastMessage.Body);
        var status = (await client.GetFromJsonAsync<MessageStatusResponse>($"/messages/{body.Id}"))!;
        Assert.Equal("sms", status.Channel);
        Assert.Equal("+821012345678", status.Recipient);
        Assert.Equal("Sent", status.Status);
    }

    [Theory]
    [InlineData(null, "Body")]
    [InlineData("+821012345678", "")]
    public async Task Post_WithAMissingField_Returns400(string? to, string? body)
    {
        var response = await CreateAuthenticatedClient().PostAsJsonAsync("/messages/sms", new SendSmsRequest(to, body));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Post_WithANumberNotInE164Form_Returns400InvalidRecipient()
    {
        var response = await CreateAuthenticatedClient().PostAsJsonAsync("/messages/sms", new SendSmsRequest("010-1234-5678", "Body"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        Assert.Equal("invalid_recipient", problem!["code"].ToString());
    }

    [Fact]
    public async Task Post_WhenTheProviderCannotTakeTheMessage_Returns400WithoutStoringIt()
    {
        _fakeSmsSender.RefuseWith("SMS to +1 numbers is not available.");
        var client = CreateAuthenticatedClient();

        var response = await client.PostAsJsonAsync("/messages/sms", new SendSmsRequest("+15551234567", "Body"));
        await TestDelivery.DeliverPendingAsync(_factory);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        Assert.Equal("invalid_request", problem!["code"].ToString());
        Assert.Equal("SMS to +1 numbers is not available.", problem["detail"].ToString());
        Assert.Null(_fakeSmsSender.LastMessage);
    }

    [Fact]
    public async Task Post_WhenTheProviderRejectsTheMessage_IsAcceptedThenReportedAsFailed()
    {
        _fakeSmsSender.FailWith(new MessageRejectedException("Solapi refused the message: 1061 opted out"));
        var client = CreateAuthenticatedClient();

        var response = await client.PostAsJsonAsync("/messages/sms", new SendSmsRequest("+821012345678", "Body"));
        var body = await response.Content.ReadFromJsonAsync<SendMessageResponse>();
        await TestDelivery.DeliverPendingAsync(_factory);

        var status = (await client.GetFromJsonAsync<MessageStatusResponse>($"/messages/{body!.Id}"))!;
        Assert.Equal("Failed", status.Status);
        Assert.Equal("Solapi refused the message: 1061 opted out", status.Error);
        Assert.Equal("message_refused", status.Reason);
        Assert.Equal(1, status.Attempts);
    }

    [Fact]
    public async Task SetCredential_Solapi_IsStoredForTheTenantAndCanBeRemoved()
    {
        var admin = CreateAdminClient();

        var put = await admin.PutAsJsonAsync($"/admin/tenants/{_tenantId}/credentials/solapi",
            new SolapiOptions { ApiKey = "key", ApiSecret = "secret", From = "0212345678" });
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);

        var store = _factory.Services.GetRequiredService<ICredentialStore>();
        Assert.Equal("0212345678", (await store.GetAsync<SolapiOptions>(_tenantId, ChannelCredentialNames.Solapi))!.From);

        var delete = await admin.DeleteAsync($"/admin/tenants/{_tenantId}/credentials/solapi");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Null(await store.GetAsync<SolapiOptions>(_tenantId, ChannelCredentialNames.Solapi));
    }

    [Fact]
    public async Task SetCredential_SolapiWithAnUnusableSenderNumber_Returns400()
    {
        var put = await CreateAdminClient().PutAsJsonAsync($"/admin/tenants/{_tenantId}/credentials/solapi",
            new SolapiOptions { ApiKey = "key", ApiSecret = "secret", From = "02-1234-5678" });

        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
    }

    private sealed class FakeSmsSender : ISmsSender
    {
        private Exception? _failure;
        private string? _refusal;

        public SmsMessage? LastMessage { get; private set; }

        public void FailWith(Exception failure) => _failure = failure;

        public void RefuseWith(string refusal) => _refusal = refusal;

        public string? Check(SmsMessage message) => _refusal;

        public Task SendAsync(SmsMessage message, CancellationToken cancellationToken = default)
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
