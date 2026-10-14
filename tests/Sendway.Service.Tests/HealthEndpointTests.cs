using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Sendway.Core;
using Sendway.Service.Health;
using Xunit;

namespace Sendway.Service.Tests;

public class HealthEndpointTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly TestIsolatedStorage _storage = new();

    public HealthEndpointTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((context, configuration) => _storage.Apply(context, configuration)));
    }

    public void Dispose() => _storage.Dispose();

    private WebApplicationFactory<Program> WithSettings(Dictionary<string, string?> settings) =>
        _factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(settings)));

    private static async Task<(HttpStatusCode Status, JsonElement Body)> GetAsync(HttpClient client, string path)
    {
        var response = await client.GetAsync(path);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        return (response.StatusCode, body);
    }

    private static async Task AddMessageAsync(WebApplicationFactory<Program> factory, DeliveryStatus status, DateTimeOffset nextAttemptAt, DateTimeOffset? completedAt = null)
    {
        await using var db = await factory.Services.GetRequiredService<IDbContextFactory<SendwayDbContext>>().CreateDbContextAsync();
        db.MessageRecords.Add(new MessageRecord
        {
            Id = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            Channel = MessageChannels.Email,
            Recipient = "user@example.com",
            AcceptedAt = nextAttemptAt,
            Status = status,
            NextAttemptAt = status is DeliveryStatus.Queued or DeliveryStatus.Sending ? nextAttemptAt : null,
            CompletedAt = completedAt
        });
        await db.SaveChangesAsync();
    }

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    [InlineData("/health")]
    public async Task Health_NeedsNoKey_AndIsHealthyWhenNothingIsWrong(string path)
    {
        var (status, body) = await GetAsync(_factory.CreateClient(), path);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("Healthy", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Live_ChecksNothing_Ready_ChecksTheDatabaseOnly_FullChecksEverything()
    {
        var client = _factory.CreateClient();

        var live = (await GetAsync(client, "/health/live")).Body.GetProperty("checks");
        var ready = (await GetAsync(client, "/health/ready")).Body.GetProperty("checks");
        var full = (await GetAsync(client, "/health")).Body.GetProperty("checks");

        Assert.Empty(live.EnumerateObject());
        Assert.Equal(["database"], ready.EnumerateObject().Select(p => p.Name));
        Assert.Equal(["database", "messages", "webhooks"], full.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal(0, full.GetProperty("messages").GetProperty("failedInLast24Hours").GetInt32());
    }

    [Fact]
    public async Task Backlog_OlderThanTheLimit_Answers503_ButLeavesReadinessAlone()
    {
        var factory = WithSettings(new() { ["Sendway:Health:MaxOldestDue"] = "00:01:00" });
        var client = factory.CreateClient();
        await AddMessageAsync(factory, DeliveryStatus.Queued, DateTimeOffset.UtcNow.AddMinutes(-2));

        var (status, body) = await GetAsync(client, "/health");
        var ready = await GetAsync(client, "/health/ready");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Equal("Unhealthy", body.GetProperty("status").GetString());
        var messages = body.GetProperty("checks").GetProperty("messages");
        Assert.Equal("Unhealthy", messages.GetProperty("status").GetString());
        Assert.InRange(messages.GetProperty("oldestDueSeconds").GetDouble(), 119, 200);
        Assert.Equal(HttpStatusCode.OK, ready.Status);
    }

    [Fact]
    public async Task AnItemWaitingForItsRetry_IsNotABacklog()
    {
        var factory = WithSettings(new() { ["Sendway:Health:MaxOldestDue"] = "00:01:00" });
        await AddMessageAsync(factory, DeliveryStatus.Queued, DateTimeOffset.UtcNow.AddMinutes(10));

        Assert.Equal(HttpStatusCode.OK, (await GetAsync(factory.CreateClient(), "/health")).Status);
    }

    [Fact]
    public async Task Failures_AreJudgedOnlyWhenALimitIsSet()
    {
        await AddMessageAsync(_factory, DeliveryStatus.Failed, DateTimeOffset.UtcNow, completedAt: DateTimeOffset.UtcNow);
        var unjudged = await GetAsync(_factory.CreateClient(), "/health");

        Assert.Equal(HttpStatusCode.OK, unjudged.Status);
        Assert.Equal(1, unjudged.Body.GetProperty("checks").GetProperty("messages").GetProperty("failedInLast24Hours").GetInt32());

        var judged = await GetAsync(WithSettings(new() { ["Sendway:Health:MaxFailedInLast24Hours"] = "0" }).CreateClient(), "/health");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, judged.Status);
        Assert.Equal("Degraded", judged.Body.GetProperty("checks").GetProperty("messages").GetProperty("status").GetString());
    }

    // Anyone can call /health, so its counts are shared for a few seconds rather than recomputed
    // on every request.
    [Fact]
    public async Task QueueCounts_AreSharedForAFewSeconds()
    {
        var factory = WithSettings(new() { ["Sendway:Health:MaxOldestDue"] = "00:01:00" });
        var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(client, "/health")).Status);

        await AddMessageAsync(factory, DeliveryStatus.Queued, DateTimeOffset.UtcNow.AddMinutes(-2));

        // Still the earlier count, though a fresh one would now see the backlog.
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(client, "/health")).Status);
        var fresh = await factory.Services.GetRequiredService<IMessageOutbox>().GetStatsAsync();
        Assert.True(fresh.OldestDueAge > TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task Health_SaysNothingAboutAnyTenantOrMessage()
    {
        var factory = WithSettings(new() { ["Sendway:Health:MaxOldestDue"] = "00:00:01" });
        await AddMessageAsync(factory, DeliveryStatus.Queued, DateTimeOffset.UtcNow.AddMinutes(-1));

        var raw = await factory.CreateClient().GetStringAsync("/health/live");
        var full = await (await factory.CreateClient().GetAsync("/health")).Content.ReadAsStringAsync();

        Assert.DoesNotContain("user@example.com", raw + full);
        Assert.DoesNotContain("tenant", (raw + full).ToLowerInvariant());
    }

    [Fact]
    public async Task DatabaseCheck_WhenTheDatabaseCannotBeOpened_IsUnhealthy_WithoutConnectionDetails()
    {
        var unreachable = Path.Combine(Path.GetTempPath(), "sendway-missing-" + Guid.NewGuid().ToString("N"), "no", "such.db");
        var options = new DbContextOptionsBuilder<SendwayDbContext>().UseSqlite($"Data Source={unreachable};Mode=ReadOnly").Options;
        var check = new DatabaseHealthCheck(new PooledDbContextFactory<SendwayDbContext>(options));

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.DoesNotContain(unreachable, result.Description);
    }
}
