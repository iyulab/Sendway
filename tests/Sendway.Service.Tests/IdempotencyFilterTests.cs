using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sendway.Core;
using Sendway.Service.Endpoints;
using Xunit;

namespace Sendway.Service.Tests;

public class IdempotencyFilterTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _baseFactory;
    private readonly TestIsolatedStorage _storage = new();

    public IdempotencyFilterTests(WebApplicationFactory<Program> factory)
    {
        _baseFactory = factory;
    }

    public void Dispose() => _storage.Dispose();

    private WebApplicationFactory<Program> CreateFactory(CountingEmailSender sender)
    {
        return _baseFactory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration(_storage.Apply);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IEmailSender>();
                services.AddSingleton<IEmailSender>(sender);
            });
        });
    }

    private static async Task<HttpClient> CreateAuthenticatedClientAsync(WebApplicationFactory<Program> factory, string tenantName = "idempotency-tenant")
    {
        var (_, apiKey) = await TestTenantSeeder.SeedAsync(factory, tenantName);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        return client;
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string? idempotencyKey = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/messages/email")
        {
            Content = JsonContent.Create(new SendEmailRequest(["user@example.com"], "Subject", "Body"))
        };
        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }
        return await client.SendAsync(request);
    }

    [Fact]
    public async Task Post_SameIdempotencyKeyTwice_SendsOnceAndReplaysResponse()
    {
        var sender = new CountingEmailSender();
        var factory = CreateFactory(sender);
        var client = await CreateAuthenticatedClientAsync(factory);

        var first = await SendAsync(client, "key-1");
        var firstBody = await first.Content.ReadFromJsonAsync<SendMessageResponse>();

        var second = await SendAsync(client, "key-1");
        var secondBody = await second.Content.ReadFromJsonAsync<SendMessageResponse>();

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(firstBody!.Id, secondBody!.Id);
        Assert.Equal(1, sender.CallCount);
    }

    [Fact]
    public async Task Post_DifferentIdempotencyKeys_SendsBoth()
    {
        var sender = new CountingEmailSender();
        var factory = CreateFactory(sender);
        var client = await CreateAuthenticatedClientAsync(factory);

        await SendAsync(client, "key-a");
        await SendAsync(client, "key-b");

        Assert.Equal(2, sender.CallCount);
    }

    [Fact]
    public async Task Post_WithoutIdempotencyKey_NeverDeduplicated()
    {
        var sender = new CountingEmailSender();
        var factory = CreateFactory(sender);
        var client = await CreateAuthenticatedClientAsync(factory);

        await SendAsync(client);
        await SendAsync(client);

        Assert.Equal(2, sender.CallCount);
    }

    [Fact]
    public async Task Post_SameKeyDifferentTenants_TrackedIndependently()
    {
        var sender = new CountingEmailSender();
        var factory = CreateFactory(sender);
        var tenantAClient = await CreateAuthenticatedClientAsync(factory, "tenant-a");
        var tenantBClient = await CreateAuthenticatedClientAsync(factory, "tenant-b");

        await SendAsync(tenantAClient, "shared-key");
        await SendAsync(tenantBClient, "shared-key");

        Assert.Equal(2, sender.CallCount);
    }

    [Fact]
    public async Task Post_IdempotencyKeyTooLong_Returns400()
    {
        var sender = new CountingEmailSender();
        var factory = CreateFactory(sender);
        var client = await CreateAuthenticatedClientAsync(factory);

        var response = await SendAsync(client, new string('k', 256));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, sender.CallCount);
    }

    [Fact]
    public async Task Post_UpstreamFailure_NotCached_RetryWithSameKeyTriesAgain()
    {
        var sender = new CountingEmailSender(failFirstNCalls: 1);
        var factory = CreateFactory(sender);
        var client = await CreateAuthenticatedClientAsync(factory);

        var first = await SendAsync(client, "retry-key");
        var second = await SendAsync(client, "retry-key");

        Assert.Equal(HttpStatusCode.InternalServerError, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(2, sender.CallCount);
    }

    private sealed class CountingEmailSender : IEmailSender
    {
        private readonly int _failFirstNCalls;

        public int CallCount { get; private set; }

        public CountingEmailSender(int failFirstNCalls = 0)
        {
            _failFirstNCalls = failFirstNCalls;
        }

        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
        {
            CallCount++;
            if (CallCount <= _failFirstNCalls)
            {
                throw new InvalidOperationException("simulated transient SMTP failure");
            }
            return Task.CompletedTask;
        }
    }
}
