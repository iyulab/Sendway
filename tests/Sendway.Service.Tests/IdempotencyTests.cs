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

public class IdempotencyTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly CountingEmailSender _sender = new();
    private readonly TestIsolatedStorage _storage = new();

    public IdempotencyTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration(_storage.Apply);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IEmailSender>();
                services.AddSingleton<IEmailSender>(_sender);
            });
        });
    }

    public void Dispose() => _storage.Dispose();

    private async Task<HttpClient> CreateAuthenticatedClientAsync(string tenantName = "idempotency-tenant")
    {
        var (_, apiKey) = await TestTenantSeeder.SeedAsync(_factory, tenantName);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        return client;
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string? idempotencyKey = null, string subject = "Subject")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/messages/email")
        {
            Content = JsonContent.Create(new SendEmailRequest(["user@example.com"], subject, "Body"))
        };
        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }
        return await client.SendAsync(request);
    }

    private static async Task<SendMessageResponse> ReadAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<SendMessageResponse>())!;

    [Fact]
    public async Task Post_SameKeyTwice_AnswersWithTheSameMessageAndSendsOnce()
    {
        var client = await CreateAuthenticatedClientAsync();

        var first = await SendAsync(client, "key-1");
        var second = await SendAsync(client, "key-1");
        await TestDelivery.DeliverPendingAsync(_factory);

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        Assert.Equal((await ReadAsync(first)).Id, (await ReadAsync(second)).Id);
        Assert.Equal(1, _sender.CallCount);
    }

    [Fact]
    public async Task Post_SameKeyAfterDelivery_AnswersWithTheDeliveredMessageWithoutSendingAgain()
    {
        var client = await CreateAuthenticatedClientAsync();

        var first = await ReadAsync(await SendAsync(client, "key-1"));
        await TestDelivery.DeliverPendingAsync(_factory);
        var second = await ReadAsync(await SendAsync(client, "key-1"));
        await TestDelivery.DeliverPendingAsync(_factory);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal("Queued", first.Status);
        Assert.Equal("Sent", second.Status);
        Assert.Equal(1, _sender.CallCount);
    }

    [Fact]
    public async Task Post_SameKeyWhileAnEarlierAttemptIsWaitingToRetry_DoesNotSendASecondCopy()
    {
        _sender.FailNextCalls(1);
        var client = await CreateAuthenticatedClientAsync();

        var first = await ReadAsync(await SendAsync(client, "retry-key"));
        await TestDelivery.DeliverPendingAsync(_factory);
        var second = await ReadAsync(await SendAsync(client, "retry-key"));
        await TestDelivery.DeliverPendingAsync(_factory);

        // The failed attempt belongs to the service to retry on its own schedule; the caller's
        // repeat request neither restarts it nor queues another copy.
        Assert.Equal(first.Id, second.Id);
        Assert.Equal("Queued", second.Status);
        Assert.Equal(1, _sender.CallCount);
    }

    [Fact]
    public async Task Post_SameKeyWithDifferentContent_Returns422()
    {
        var client = await CreateAuthenticatedClientAsync();

        await SendAsync(client, "key-1", subject: "First subject");
        var second = await SendAsync(client, "key-1", subject: "Another subject");
        await TestDelivery.DeliverPendingAsync(_factory);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, second.StatusCode);
        Assert.Equal(1, _sender.CallCount);
    }

    [Fact]
    public async Task Post_DifferentKeys_SendsBoth()
    {
        var client = await CreateAuthenticatedClientAsync();

        await SendAsync(client, "key-a");
        await SendAsync(client, "key-b");
        await TestDelivery.DeliverPendingAsync(_factory);

        Assert.Equal(2, _sender.CallCount);
    }

    [Fact]
    public async Task Post_WithoutKey_NeverDeduplicated()
    {
        var client = await CreateAuthenticatedClientAsync();

        await SendAsync(client);
        await SendAsync(client);
        await TestDelivery.DeliverPendingAsync(_factory);

        Assert.Equal(2, _sender.CallCount);
    }

    [Fact]
    public async Task Post_SameKeyDifferentTenants_TrackedIndependently()
    {
        var tenantAClient = await CreateAuthenticatedClientAsync("tenant-a");
        var tenantBClient = await CreateAuthenticatedClientAsync("tenant-b");

        var first = await ReadAsync(await SendAsync(tenantAClient, "shared-key"));
        var second = await ReadAsync(await SendAsync(tenantBClient, "shared-key"));
        await TestDelivery.DeliverPendingAsync(_factory);

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(2, _sender.CallCount);
    }

    [Fact]
    public async Task Post_KeyTooLong_Returns400()
    {
        var client = await CreateAuthenticatedClientAsync();

        var response = await SendAsync(client, new string('k', 256));
        await TestDelivery.DeliverPendingAsync(_factory);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, _sender.CallCount);
    }

    private sealed class CountingEmailSender : IEmailSender
    {
        private int _failuresRemaining;

        public int CallCount { get; private set; }

        public void FailNextCalls(int count) => _failuresRemaining = count;

        public Task<EmailDelivery> SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
        {
            CallCount++;
            if (_failuresRemaining > 0)
            {
                _failuresRemaining--;
                throw new InvalidOperationException("simulated transient SMTP failure");
            }
            return Task.FromResult(EmailDelivery.ToEveryone);
        }
    }
}
