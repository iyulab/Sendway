using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sendway.Core;
using Sendway.Service.Endpoints;
using Sendway.Service.Endpoints.Admin;
using Xunit;

namespace Sendway.Service.Tests;

// The shape every refused request shares: status, a stable `code`, a human-readable `detail`.
internal sealed record ProblemResponse(int Status, string Code, string Detail);

public class ApiProblemTests : IClassFixture<WebApplicationFactory<Program>>, IAsyncLifetime
{
    private const string AdminApiKey = "test-admin-key";

    private readonly WebApplicationFactory<Program> _factory;
    private readonly TestIsolatedStorage _storage = new();
    private Guid _tenantId;
    private string _apiKey = null!;

    public ApiProblemTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((context, configuration) =>
            {
                _storage.Apply(context, configuration);
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Sendway:AdminApiKey"] = AdminApiKey,
                    ["Sendway:RateLimit:RequestsPerMinute"] = "3"
                });
            });
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IEmailSender>();
                services.AddSingleton<IEmailSender, NoopEmailSender>();
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

    private HttpClient CreateTenantClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", _apiKey);
        return client;
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var problem = await response.Content.ReadFromJsonAsync<ProblemResponse>();
        Assert.Equal((int)status, problem!.Status);
        Assert.Equal(code, problem.Code);
        Assert.False(string.IsNullOrWhiteSpace(problem.Detail));
        Assert.DoesNotMatch("[\\uAC00-\\uD7A3]", problem.Detail);
    }

    [Fact]
    public async Task MissingField_IsAnInvalidRequest()
    {
        var response = await CreateTenantClient().PostAsJsonAsync("/messages/email", new SendEmailRequest(null, "Subject", "Body"));

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "invalid_request");
    }

    [Fact]
    public async Task OversizedPushField_IsAnInvalidRequest()
    {
        var response = await CreateTenantClient().PostAsJsonAsync(
            "/messages/push",
            new SendPushRequest("device-token", new string('t', 201), "Body"));

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "invalid_request");
    }

    [Fact]
    public async Task MalformedAddress_IsAnInvalidRecipient()
    {
        var response = await CreateTenantClient().PostAsJsonAsync(
            "/messages/email",
            new SendEmailRequest(["not-an-email-address"], "Subject", "Body"));

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "invalid_recipient");
    }

    [Fact]
    public async Task KeyReusedForAnotherRequest_IsReportedAsSuch()
    {
        var client = CreateTenantClient();
        client.DefaultRequestHeaders.Add("Idempotency-Key", "key-1");
        await client.PostAsJsonAsync("/messages/email", new SendEmailRequest(["user@example.com"], "First", "Body"));

        var response = await client.PostAsJsonAsync("/messages/email", new SendEmailRequest(["user@example.com"], "Second", "Body"));

        await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, "idempotency_key_reused");
    }

    [Fact]
    public async Task ExceedingTheRateLimit_IsReportedWithRetryAfter()
    {
        var client = CreateTenantClient();
        HttpResponseMessage response = null!;
        for (var i = 0; i < 4; i++)
        {
            response = await client.PostAsJsonAsync("/messages/email", new SendEmailRequest(["user@example.com"], "Subject", "Body"));
        }

        await AssertProblemAsync(response, HttpStatusCode.TooManyRequests, "rate_limited");
        Assert.True(response.Headers.Contains("Retry-After"));
    }

    [Fact]
    public async Task AdminValidationFailure_UsesTheSameShape()
    {
        var admin = _factory.CreateClient();
        admin.DefaultRequestHeaders.Add("X-Admin-Key", AdminApiKey);

        var response = await admin.PutAsJsonAsync($"/admin/tenants/{_tenantId}/webhook", new SetTenantWebhookRequest("ftp://receiver.example.com"));

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "invalid_request");
    }

    private sealed class NoopEmailSender : IEmailSender
    {
        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
