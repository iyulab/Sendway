using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Sendway.Service.Tests;

// What a calling service sees: start a verification, enter the code, check it. Sandbox tenants are used for the
// flows (they are the ones that get the code back, and nothing leaves the instance); one real tenant shows that
// it does not.
public class VerificationEndpointTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private const string AdminApiKey = "test-admin-key";

    private readonly TestIsolatedStorage _storage = new();
    private readonly WebApplicationFactory<Program> _factory;

    public VerificationEndpointTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((context, configuration) =>
            {
                _storage.Apply(context, configuration);
                configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Sendway:AdminApiKey"] = AdminApiKey });
            });
        });
    }

    public void Dispose() => _storage.Dispose();

    private async Task<HttpClient> SandboxClientAsync(string name)
    {
        var admin = _factory.CreateClient();
        admin.DefaultRequestHeaders.Add("X-Admin-Key", AdminApiKey);
        var created = await (await admin.PostAsJsonAsync("/admin/tenants", new { name = name + "-" + Guid.NewGuid().ToString("N")[..6], sandbox = true }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", created.GetProperty("apiKey").GetString());
        return client;
    }

    private static Task<HttpResponseMessage> Start(HttpClient client, object body, string? key = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/verifications") { Content = JsonContent.Create(body) };
        if (key is not null)
        {
            request.Headers.Add("Idempotency-Key", key);
        }

        return client.SendAsync(request);
    }

    private static async Task<(Guid Id, string Code)> StartedAsync(HttpClient client, object body)
    {
        var response = await Start(client, body);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (json.GetProperty("id").GetGuid(), json.GetProperty("code").GetString()!);
    }

    private static async Task<JsonElement> CheckAsync(HttpClient client, Guid id, string code)
    {
        var response = await client.PostAsJsonAsync($"/verifications/{id}/check", new { code });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<string> ProblemCode(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<Dictionary<string, object>>())!["code"].ToString()!;

    [Fact]
    public async Task ASandboxTenant_StartsGetsTheCodeAndVerifiesItOnce()
    {
        var client = await SandboxClientAsync("flow");

        var (id, code) = await StartedAsync(client, new { channel = "email", to = "user@example.com", purpose = "signup" });

        Assert.Equal(6, code.Length);
        Assert.Equal("verified", (await CheckAsync(client, id, code)).GetProperty("status").GetString());
        Assert.Equal("expired", (await CheckAsync(client, id, code)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task ARealTenant_IsNeverTold_TheCode()
    {
        var admin = _factory.CreateClient();
        admin.DefaultRequestHeaders.Add("X-Admin-Key", AdminApiKey);
        var (_, apiKey) = await TestTenantSeeder.SeedAsync(_factory, "real-" + Guid.NewGuid().ToString("N")[..6]);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);

        var response = await Start(client, new { channel = "email", to = "user@example.com" });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("id").GetGuid() != Guid.Empty);
        Assert.True(!json.TryGetProperty("code", out var code) || code.ValueKind == JsonValueKind.Null);
        Assert.NotNull(response.Headers.Location);
    }

    [Fact]
    public async Task AWrongCode_SaysHowManyTriesAreLeft_ThenTheCodeIsDead()
    {
        var client = await SandboxClientAsync("wrong");
        var (id, code) = await StartedAsync(client, new { channel = "sms", to = "+821012345678", maxAttempts = 2 });
        var wrong = code == "000000" ? "111111" : "000000";

        var first = await CheckAsync(client, id, wrong);
        var second = await CheckAsync(client, id, wrong);
        var third = await CheckAsync(client, id, code);

        Assert.Equal(("mismatch", 1), (first.GetProperty("status").GetString(), first.GetProperty("attemptsLeft").GetInt32()));
        Assert.Equal(("mismatch", 0), (second.GetProperty("status").GetString(), second.GetProperty("attemptsLeft").GetInt32()));
        Assert.Equal("too_many_attempts", third.GetProperty("status").GetString());
    }

    [Fact]
    public async Task TheStatus_ShowsWhereItStandsAndHowTheMessageWent()
    {
        var client = await SandboxClientAsync("status");
        var (id, _) = await StartedAsync(client, new { channel = "email", to = "user@example.com", purpose = "login", codeLength = 8 });

        var response = await client.GetFromJsonAsync<JsonElement>($"/verifications/{id}");

        Assert.Equal("pending", response.GetProperty("status").GetString());
        Assert.Equal("email", response.GetProperty("channel").GetString());
        Assert.Equal("login", response.GetProperty("purpose").GetString());
        Assert.Equal(5, response.GetProperty("attemptsLeft").GetInt32());
        Assert.Contains(response.GetProperty("delivery").GetProperty("status").GetString(), new[] { "Queued", "Sending", "Sent" });
        Assert.False(response.TryGetProperty("destination", out _));
        Assert.False(response.TryGetProperty("code", out _));
    }

    [Fact]
    public async Task AnotherTenant_GetsNotFound_AndCannotUseUpTheTries()
    {
        var owner = await SandboxClientAsync("owner");
        var stranger = await SandboxClientAsync("stranger");
        var (id, code) = await StartedAsync(owner, new { channel = "email", to = "user@example.com" });

        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PostAsJsonAsync($"/verifications/{id}/check", new { code })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/verifications/{id}")).StatusCode);
        Assert.Equal("verified", (await CheckAsync(owner, id, code)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task AskingAgainTooSoon_Is429WithRetryAfter_AndNothingNewIsSent()
    {
        var client = await SandboxClientAsync("soon");
        var (_, firstCode) = await StartedAsync(client, new { channel = "email", to = "user@example.com" });

        var again = await Start(client, new { channel = "email", to = "User <USER@example.com>" });

        Assert.Equal(HttpStatusCode.TooManyRequests, again.StatusCode);
        Assert.Equal("destination_rate_limited", await ProblemCode(again));
        Assert.InRange(again.Headers.RetryAfter!.Delta!.Value.TotalSeconds, 1, 30);
        Assert.NotEmpty(firstCode);
    }

    [Fact]
    public async Task ARepeatedKey_ReturnsTheSameVerification_ADifferentRequestWithItIs422()
    {
        var client = await SandboxClientAsync("key");
        var body = new { channel = "email", to = "user@example.com" };

        var first = await (await Start(client, body, "k-1")).Content.ReadFromJsonAsync<JsonElement>();
        var replay = await (await Start(client, body, "k-1")).Content.ReadFromJsonAsync<JsonElement>();
        var different = await Start(client, new { channel = "email", to = "user@example.com", codeLength = 8 }, "k-1");

        Assert.Equal(first.GetProperty("id").GetGuid(), replay.GetProperty("id").GetGuid());
        Assert.Equal(first.GetProperty("code").GetString(), replay.GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, different.StatusCode);
        Assert.Equal("idempotency_key_reused", await ProblemCode(different));
    }

    [Theory]
    [InlineData("""{"channel":"push","to":"x"}""")]
    [InlineData("""{"channel":"email"}""")]
    [InlineData("""{"channel":"email","to":"user@example.com","ttlSeconds":5}""")]
    [InlineData("""{"channel":"email","to":"user@example.com","ttlSeconds":100000}""")]
    [InlineData("""{"channel":"email","to":"user@example.com","codeLength":3}""")]
    [InlineData("""{"channel":"email","to":"user@example.com","codeLength":9}""")]
    [InlineData("""{"channel":"email","to":"user@example.com","maxAttempts":0}""")]
    [InlineData("""{"channel":"email","to":"user@example.com","maxAttempts":11}""")]
    [InlineData("""{"channel":"email","to":"user@example.com","locale":"fr"}""")]
    public async Task ARequestOutsideTheBounds_Is400(string json)
    {
        var client = await SandboxClientAsync("bounds");

        var response = await client.PostAsync("/verifications", new StringContent(json, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_request", await ProblemCode(response));
    }

    [Fact]
    public async Task AMalformedDestination_IsAnInvalidRecipient_AndASmsAbroadIsNotAllowedByDefault()
    {
        var client = await SandboxClientAsync("destination");

        var badEmail = await Start(client, new { channel = "email", to = "not-an-address" });
        var nationalSms = await Start(client, new { channel = "sms", to = "01012345678" });
        var abroad = await Start(client, new { channel = "sms", to = "+819012345678" });

        Assert.Equal("invalid_recipient", await ProblemCode(badEmail));
        Assert.Equal("invalid_recipient", await ProblemCode(nationalSms));
        Assert.Equal(HttpStatusCode.BadRequest, abroad.StatusCode);
        Assert.Equal("destination_not_allowed", await ProblemCode(abroad));
    }

    [Fact]
    public async Task ACheckWithoutACode_Is400_AndWithoutAKeyIs401()
    {
        var client = await SandboxClientAsync("nocode");
        var (id, _) = await StartedAsync(client, new { channel = "email", to = "user@example.com" });

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/verifications/{id}/check", new { code = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().PostAsJsonAsync($"/verifications/{id}/check", new { code = "123456" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().PostAsJsonAsync("/verifications", new { channel = "email", to = "a@example.com" })).StatusCode);
    }
}
