using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sendway.Core;
using Sendway.Service.Endpoints;
using Xunit;

namespace Sendway.Service.Tests;

public class TenantRateLimitTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _baseFactory;
    private readonly TestIsolatedStorage _storage = new();

    public TenantRateLimitTests(WebApplicationFactory<Program> factory)
    {
        _baseFactory = factory;
    }

    public void Dispose() => _storage.Dispose();

    private WebApplicationFactory<Program> CreateFactory(string requestsPerMinute = "2", string enabled = "true")
    {
        return _baseFactory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((context, configuration) =>
            {
                _storage.Apply(context, configuration);
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Sendway:RateLimit:RequestsPerMinute"] = requestsPerMinute,
                    ["Sendway:RateLimit:Enabled"] = enabled
                });
            });
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IEmailSender>();
                services.AddSingleton<IEmailSender, NoopEmailSender>();
            });
        });
    }

    private static async Task<HttpClient> CreateAuthenticatedClientAsync(WebApplicationFactory<Program> factory, string tenantName = "rate-limit-tenant")
    {
        var (_, apiKey) = await TestTenantSeeder.SeedAsync(factory, tenantName);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        return client;
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client) =>
        client.PostAsJsonAsync("/messages/email", new SendEmailRequest(["user@example.com"], "Subject", "Body"));

    [Fact]
    public async Task Post_ExceedsConfiguredLimit_Returns429WithRetryAfter()
    {
        var factory = CreateFactory(requestsPerMinute: "2");
        var client = await CreateAuthenticatedClientAsync(factory);

        Assert.Equal(HttpStatusCode.Accepted, (await SendAsync(client)).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await SendAsync(client)).StatusCode);

        var thirdResponse = await SendAsync(client);

        Assert.Equal(HttpStatusCode.TooManyRequests, thirdResponse.StatusCode);
        Assert.True(thirdResponse.Headers.TryGetValues("Retry-After", out var retryAfter));
        Assert.True(int.Parse(retryAfter!.First()) > 0);
    }

    [Fact]
    public async Task Post_DifferentTenants_TrackedIndependently()
    {
        var factory = CreateFactory(requestsPerMinute: "1");
        var tenantAClient = await CreateAuthenticatedClientAsync(factory, "tenant-a");
        var tenantBClient = await CreateAuthenticatedClientAsync(factory, "tenant-b");

        Assert.Equal(HttpStatusCode.Accepted, (await SendAsync(tenantAClient)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await SendAsync(tenantAClient)).StatusCode);

        // Tenant B's own window is untouched by tenant A hitting its limit.
        Assert.Equal(HttpStatusCode.Accepted, (await SendAsync(tenantBClient)).StatusCode);
    }

    [Fact]
    public async Task Post_WhenDisabled_NeverLimited()
    {
        var factory = CreateFactory(requestsPerMinute: "1", enabled: "false");
        var client = await CreateAuthenticatedClientAsync(factory);

        Assert.Equal(HttpStatusCode.Accepted, (await SendAsync(client)).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await SendAsync(client)).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await SendAsync(client)).StatusCode);
    }

    [Fact]
    public async Task Post_OverTheLimit_IsRefusedBeforeItsBodyIsRead()
    {
        var factory = CreateFactory(requestsPerMinute: "1");
        var client = await CreateAuthenticatedClientAsync(factory);
        Assert.Equal(HttpStatusCode.Accepted, (await SendAsync(client)).StatusCode);

        var response = await client.PostAsync("/messages/email", new StringContent("{bad", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
    }

    [Fact]
    public async Task Post_ConcurrentRequests_NeverExceedTheLimit()
    {
        var factory = CreateFactory(requestsPerMinute: "5");
        var client = await CreateAuthenticatedClientAsync(factory);

        var responses = await Task.WhenAll(Enumerable.Range(0, 40).Select(_ => Task.Run(() => SendAsync(client))));

        Assert.Equal(5, responses.Count(response => response.StatusCode == HttpStatusCode.Accepted));
        Assert.Equal(35, responses.Count(response => response.StatusCode == HttpStatusCode.TooManyRequests));
    }

    private sealed class NoopEmailSender : IEmailSender
    {
        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
