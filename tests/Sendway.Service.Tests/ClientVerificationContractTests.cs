using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Sendway.Client;
using Xunit;

namespace Sendway.Service.Tests;

// The published client's verification calls against the running service, in the client's own types.
public class ClientVerificationContractTests : IClassFixture<WebApplicationFactory<Program>>, IAsyncLifetime
{
    private const string AdminApiKey = "test-admin-key";

    private readonly WebApplicationFactory<Program> _factory;
    private readonly TestIsolatedStorage _storage = new();
    private SendwayClient _client = null!;

    public ClientVerificationContractTests(WebApplicationFactory<Program> factory)
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

    public async Task InitializeAsync()
    {
        var admin = _factory.CreateClient();
        admin.DefaultRequestHeaders.Add("X-Admin-Key", AdminApiKey);
        var created = await (await admin.PostAsJsonAsync("/admin/tenants", new { name = "client-verify", sandbox = true })).Content.ReadFromJsonAsync<JsonElement>();
        _client = new SendwayClient(_factory.CreateClient(), created.GetProperty("apiKey").GetString()!);
    }

    public Task DisposeAsync()
    {
        _storage.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task StartCheckAndRead_InTheClientsTypes()
    {
        var started = await _client.StartVerificationAsync(
            new VerificationRequest(VerificationChannel.Sms, "+821012345678") { Purpose = "signup", CodeLength = 8, MaxAttempts = 2, Locale = "ko" });

        Assert.Equal(8, started.Code!.Length);
        Assert.True(started.ExpiresAt > DateTimeOffset.UtcNow);

        var pending = await _client.GetVerificationAsync(started.Id);
        Assert.Equal(VerificationState.Pending, pending.State);
        Assert.Equal("sms", pending.Channel);
        Assert.Equal("signup", pending.Purpose);
        Assert.Equal(2, pending.AttemptsLeft);

        var wrong = await _client.CheckVerificationAsync(started.Id, started.Code == "00000000" ? "11111111" : "00000000");
        Assert.Equal(VerificationResult.Mismatch, wrong.Result);
        Assert.Equal(1, wrong.AttemptsLeft);

        var right = await _client.CheckVerificationAsync(started.Id, started.Code);
        Assert.Equal(VerificationResult.Verified, right.Result);
        Assert.Equal(VerificationResult.Expired, (await _client.CheckVerificationAsync(started.Id, started.Code)).Result);
        Assert.Equal(VerificationState.Verified, (await _client.GetVerificationAsync(started.Id)).State);
    }

    [Fact]
    public async Task AskingAgainTooSoon_IsAnExceptionWithTheCodeAndHowLongToWait()
    {
        await _client.StartVerificationAsync(new VerificationRequest(VerificationChannel.Email, "user@example.com"));

        var refused = await Assert.ThrowsAsync<SendwayException>(() =>
            _client.StartVerificationAsync(new VerificationRequest(VerificationChannel.Email, "user@example.com")));

        Assert.Equal(SendwayErrorCode.DestinationRateLimited, refused.Code);
        Assert.InRange(refused.RetryAfter!.Value.TotalSeconds, 1, 30);
    }

    [Fact]
    public async Task ARepeatedKey_AnswersWithTheOriginal()
    {
        var request = new VerificationRequest(VerificationChannel.Email, "keyed@example.com");

        var first = await _client.StartVerificationAsync(request, "once");
        var again = await _client.StartVerificationAsync(request, "once");

        Assert.Equal(first.Id, again.Id);
        Assert.Equal(first.Code, again.Code);
    }

    [Fact]
    public async Task AnUnknownVerification_IsNotFound()
    {
        var missing = await Assert.ThrowsAsync<SendwayException>(() => _client.CheckVerificationAsync(Guid.NewGuid(), "123456"));

        Assert.Equal(SendwayErrorCode.NotFound, missing.Code);
    }
}
