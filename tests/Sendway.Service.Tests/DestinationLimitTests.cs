using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Sendway.Service.Endpoints;
using Xunit;

namespace Sendway.Service.Tests;

// What a caller sees when one destination is sent to too recently or too often. Sandbox tenants are
// used throughout: they run the same acceptance path (the rule applies to them too) and nothing leaves
// the instance, so no provider has to be faked.
public class DestinationLimitTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private const string AdminApiKey = "test-admin-key";

    private readonly TestIsolatedStorage _storage = new();
    private readonly WebApplicationFactory<Program> _factory;

    public DestinationLimitTests(WebApplicationFactory<Program> factory)
    {
        _factory = Configure(factory, new Dictionary<string, string?>());
    }

    public void Dispose() => _storage.Dispose();

    private WebApplicationFactory<Program> Configure(WebApplicationFactory<Program> factory, Dictionary<string, string?> settings) =>
        factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((context, configuration) =>
            {
                _storage.Apply(context, configuration);
                configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Sendway:AdminApiKey"] = AdminApiKey });
                configuration.AddInMemoryCollection(settings);
            });
        });

    private static HttpClient Admin(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Admin-Key", AdminApiKey);
        return client;
    }

    private static async Task<(Guid Id, HttpClient Client)> SandboxTenantAsync(WebApplicationFactory<Program> factory, string name)
    {
        var created = await Admin(factory).PostAsJsonAsync("/admin/tenants", new { name, sandbox = true });
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", body.GetProperty("apiKey").GetString());
        return (body.GetProperty("tenantId").GetGuid(), client);
    }

    private static Task<HttpResponseMessage> Email(HttpClient client, string to, string? key = null, List<string>? cc = null) =>
        client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/messages/email")
        {
            Content = JsonContent.Create(new SendEmailRequest([to], "Subject", "Body", cc)),
            Headers = { { "Idempotency-Key", key ?? Guid.NewGuid().ToString() } }
        });

    private static Task<HttpResponseMessage> Sms(HttpClient client, string to) =>
        client.PostAsJsonAsync("/messages/sms", new SendSmsRequest(to, "Body"));

    private static async Task AssertDestinationLimited(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        Assert.Equal("destination_rate_limited", problem!["code"].ToString());
        Assert.NotNull(response.Headers.RetryAfter?.Delta);
    }

    private static Task SetLimits(WebApplicationFactory<Program> factory, Guid tenant, object limits) =>
        Admin(factory).PutAsJsonAsync($"/admin/tenants/{tenant}/limits", limits);

    [Fact]
    public async Task ByDefault_SmsToOneNumber_IsLimitedToFivePerHour_OtherNumbersAreNot()
    {
        var (_, client) = await SandboxTenantAsync(_factory, "sms-default");

        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(HttpStatusCode.Accepted, (await Sms(client, "+821012345678")).StatusCode);
        }

        await AssertDestinationLimited(await Sms(client, "+821012345678"));
        Assert.Equal(HttpStatusCode.Accepted, (await Sms(client, "+821099999999")).StatusCode);
    }

    [Fact]
    public async Task ByDefault_ThereIsNoCooldown_TwoEmailsInARowToOneAddressAreBothAccepted()
    {
        var (_, client) = await SandboxTenantAsync(_factory, "no-cooldown");

        Assert.Equal(HttpStatusCode.Accepted, (await Email(client, "user@example.com")).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await Email(client, "user@example.com")).StatusCode);
    }

    [Fact]
    public async Task ATenantsCooldown_RefusesTheSecondMessage_WithRetryAfter_AndAReplayStillAnswers()
    {
        var (id, client) = await SandboxTenantAsync(_factory, "cooldown");
        Assert.Equal(HttpStatusCode.NoContent, (await Admin(_factory).PutAsJsonAsync($"/admin/tenants/{id}/limits", new { destinationCooldownSeconds = 60 })).StatusCode);

        var first = await Email(client, "user@example.com", key: "code-1");
        var replay = await Email(client, "user@example.com", key: "code-1");
        var tooSoon = await Email(client, "user@example.com", key: "code-2");
        var other = await Email(client, "other@example.com", key: "code-3");

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, replay.StatusCode);
        await AssertDestinationLimited(tooSoon);
        Assert.InRange(tooSoon.Headers.RetryAfter!.Delta!.Value.TotalSeconds, 1, 60);
        Assert.Equal(HttpStatusCode.Accepted, other.StatusCode);
    }

    [Fact]
    public async Task APerHourLimit_AppliesToEveryChannel_AndEmailAddressesIgnoreCase()
    {
        var (id, client) = await SandboxTenantAsync(_factory, "per-hour");
        await SetLimits(_factory, id, new { destinationPerHour = 2 });

        Assert.Equal(HttpStatusCode.Accepted, (await Email(client, "User@Example.com")).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await Email(client, "user@example.com")).StatusCode);
        await AssertDestinationLimited(await Email(client, "USER@EXAMPLE.COM"));

        Assert.Equal(HttpStatusCode.Accepted, (await Sms(client, "+821012345678")).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await Sms(client, "+821012345678")).StatusCode);
        await AssertDestinationLimited(await Sms(client, "+821012345678"));
    }

    [Theory]
    [InlineData("Name <user@example.com>")]
    [InlineData("\"Someone Else\" <USER@EXAMPLE.COM>")]
    [InlineData("  user@Example.Com ")]
    public async Task AnAddressWrittenAnotherWay_IsTheSameDestination(string spelling)
    {
        var (id, client) = await SandboxTenantAsync(_factory, "spelling-" + Guid.NewGuid().ToString("N")[..8]);
        await SetLimits(_factory, id, new { destinationPerHour = 1 });

        Assert.Equal(HttpStatusCode.Accepted, (await Email(client, "user@example.com")).StatusCode);

        await AssertDestinationLimited(await Email(client, spelling));
    }

    [Fact]
    public async Task PushToOneDeviceToken_IsCountedToo()
    {
        var (id, client) = await SandboxTenantAsync(_factory, "push");
        await SetLimits(_factory, id, new { destinationPerHour = 1 });

        var first = await client.PostAsJsonAsync("/messages/push", new SendPushRequest("token-1", "T", "B"));
        var second = await client.PostAsJsonAsync("/messages/push", new SendPushRequest("token-1", "T", "B"));
        var otherDevice = await client.PostAsJsonAsync("/messages/push", new SendPushRequest("token-2", "T", "B"));

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        await AssertDestinationLimited(second);
        Assert.Equal(HttpStatusCode.Accepted, otherDevice.StatusCode);
    }

    [Fact]
    public async Task AnEmailWhoseRecipientsAreAllOneAddress_IsCountedAsThatDestination()
    {
        var (id, client) = await SandboxTenantAsync(_factory, "one-address");
        await SetLimits(_factory, id, new { destinationPerHour = 1 });

        // The same address in to and cc, written two ways, is still one mailbox.
        Assert.Equal(HttpStatusCode.Accepted, (await Email(client, "user@example.com", cc: ["User <USER@example.com>"])).StatusCode);

        await AssertDestinationLimited(await Email(client, "user@example.com"));
    }

    [Fact]
    public async Task AnEmailToSeveralRecipients_IsNotCountedAgainstAnyOfThem()
    {
        var (id, client) = await SandboxTenantAsync(_factory, "several");
        await SetLimits(_factory, id, new { destinationPerHour = 1 });

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.Accepted, (await Email(client, "a@example.com", cc: ["b@example.com"])).StatusCode);
        }

        // ...and it does not use up the single-recipient allowance either.
        Assert.Equal(HttpStatusCode.Accepted, (await Email(client, "a@example.com")).StatusCode);
        await AssertDestinationLimited(await Email(client, "a@example.com"));
    }

    [Fact]
    public async Task AnotherTenant_IsNotHeldBackByThisOnesHistory()
    {
        var (first, firstClient) = await SandboxTenantAsync(_factory, "tenant-one");
        var (_, secondClient) = await SandboxTenantAsync(_factory, "tenant-two");
        await SetLimits(_factory, first, new { destinationPerHour = 1 });

        await Email(firstClient, "user@example.com");
        await AssertDestinationLimited(await Email(firstClient, "user@example.com"));

        Assert.Equal(HttpStatusCode.Accepted, (await Email(secondClient, "user@example.com")).StatusCode);
    }

    [Fact]
    public async Task ADeploymentCooldown_IsTheDefault_AndATenantCanTurnItOffWithZero()
    {
        using var factory = Configure(_factory, new Dictionary<string, string?> { ["Sendway:Limits:DestinationCooldown"] = "00:01:00" });
        var (_, held) = await SandboxTenantAsync(factory, "default-cooldown");
        var (id, free) = await SandboxTenantAsync(factory, "cooldown-off");
        await SetLimits(factory, id, new { destinationCooldownSeconds = 0 });

        await Email(held, "user@example.com");
        await AssertDestinationLimited(await Email(held, "user@example.com"));

        await Email(free, "user@example.com");
        Assert.Equal(HttpStatusCode.Accepted, (await Email(free, "user@example.com")).StatusCode);
    }

    [Fact]
    public async Task TheTenantList_ShowsTheLimitsInEffect_AndRejectsNegativeValues()
    {
        var (id, _) = await SandboxTenantAsync(_factory, "listed");

        var bad = await Admin(_factory).PutAsJsonAsync($"/admin/tenants/{id}/limits", new { destinationPerHour = -1 });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var before = await TenantAsync(id);
        Assert.Equal(5, before.GetProperty("destination").GetProperty("smsPerHour").GetInt32());
        Assert.Equal(30, before.GetProperty("destination").GetProperty("emailPerHour").GetInt32());
        Assert.Equal(60, before.GetProperty("destination").GetProperty("pushPerHour").GetInt32());
        Assert.Equal(JsonValueKind.Null, before.GetProperty("destination").GetProperty("cooldownSeconds").ValueKind);

        await SetLimits(_factory, id, new { destinationPerHour = 9, destinationCooldownSeconds = 45 });

        var after = await TenantAsync(id);
        Assert.Equal(9, after.GetProperty("destination").GetProperty("smsPerHour").GetInt32());
        Assert.Equal(9, after.GetProperty("destination").GetProperty("emailPerHour").GetInt32());
        Assert.Equal(45, after.GetProperty("destination").GetProperty("cooldownSeconds").GetInt32());
    }

    private async Task<JsonElement> TenantAsync(Guid id)
    {
        var tenants = await Admin(_factory).GetFromJsonAsync<JsonElement>("/admin/tenants");
        return tenants.EnumerateArray().Single(t => t.GetProperty("id").GetGuid() == id);
    }
}
