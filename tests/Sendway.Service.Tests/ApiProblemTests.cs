using System.Net;
using System.Net.Http.Json;
using System.Text;
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

    // The errors below are answered before any endpoint code runs (or after it failed), so no
    // endpoint chose their shape.

    [Fact]
    public async Task UnreadableBody_IsAnInvalidRequest()
    {
        var response = await CreateTenantClient().PostAsync(
            "/messages/email",
            new StringContent("{bad", Encoding.UTF8, "application/json"));

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "invalid_request");
    }

    [Fact]
    public async Task BodyThatIsNotJson_IsAnInvalidRequest()
    {
        var response = await CreateTenantClient().PostAsync(
            "/messages/email",
            new StringContent("to=user@example.com", Encoding.UTF8, "text/plain"));

        await AssertProblemAsync(response, HttpStatusCode.UnsupportedMediaType, "invalid_request");
    }

    [Fact]
    public async Task MissingApiKey_IsUnauthorized()
    {
        var response = await _factory.CreateClient().PostAsJsonAsync(
            "/messages/email",
            new SendEmailRequest(["user@example.com"], "Subject", "Body"));

        await AssertProblemAsync(response, HttpStatusCode.Unauthorized, "unauthorized");
    }

    [Fact]
    public async Task MissingAdminKey_IsUnauthorized()
    {
        var response = await _factory.CreateClient().GetAsync("/admin/tenants");

        await AssertProblemAsync(response, HttpStatusCode.Unauthorized, "unauthorized");
    }

    // Who is asking is settled before what they sent is read. (A path, method or content type
    // that matches no endpoint is still answered as such: no endpoint, nobody to authenticate to.)
    [Theory]
    [InlineData("/messages/email")]
    [InlineData("/admin/tenants")]
    public async Task UnreadableBodyWithoutAKey_IsUnauthorized(string path)
    {
        var response = await _factory.CreateClient().PostAsync(path, new StringContent("{bad", Encoding.UTF8, "application/json"));

        await AssertProblemAsync(response, HttpStatusCode.Unauthorized, "unauthorized");
    }

    [Fact]
    public async Task UnknownMessage_IsNotFound()
    {
        var response = await CreateTenantClient().GetAsync($"/messages/{Guid.NewGuid()}");

        await AssertProblemAsync(response, HttpStatusCode.NotFound, "not_found");
    }

    [Fact]
    public async Task UnknownPath_IsNotFound()
    {
        var response = await _factory.CreateClient().GetAsync("/no-such-path");

        await AssertProblemAsync(response, HttpStatusCode.NotFound, "not_found");
    }

    [Fact]
    public async Task WrongMethod_IsAnInvalidRequest()
    {
        var response = await CreateTenantClient().DeleteAsync("/messages/email");

        await AssertProblemAsync(response, HttpStatusCode.MethodNotAllowed, "invalid_request");
    }

    [Fact]
    public async Task UnexpectedFailure_IsAnInternalErrorThatSaysNothingAboutItsCause()
    {
        var failing = _factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IMessageOutbox>();
            services.AddSingleton<IMessageOutbox, UnavailableOutbox>();
        }));
        var client = failing.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", _apiKey);

        var response = await client.GetAsync($"/messages/{Guid.NewGuid()}");

        await AssertProblemAsync(response, HttpStatusCode.InternalServerError, "internal_error");
        Assert.DoesNotContain(UnavailableOutbox.Cause, await response.Content.ReadAsStringAsync());
    }

    private sealed class UnavailableOutbox : IMessageOutbox
    {
        public const string Cause = "the storage is unreachable";

        public Task<EnqueueResult> EnqueueAsync(MessageSubmission submission, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(Cause);

        public Task<MessageRecord?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(Cause);

        // The delivery worker polls this; an empty queue keeps it idle.
        public Task<ClaimedMessage?> ClaimNextAsync(TimeSpan lease, CancellationToken cancellationToken = default) =>
            Task.FromResult<ClaimedMessage?>(null);

        public string ReadPayload(ClaimedMessage message) => throw new InvalidOperationException(Cause);

        public Task MarkSentAsync(ClaimedMessage claim, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task MarkFailedAsync(ClaimedMessage claim, string error, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ScheduleRetryAsync(ClaimedMessage claim, string error, TimeSpan delay, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ReleaseAsync(ClaimedMessage claim, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<QueueStats> GetStatsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new QueueStats(0, 0, 0, null));
    }

    private sealed class NoopEmailSender : IEmailSender
    {
        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
