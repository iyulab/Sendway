using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sendway.Core;
using Sendway.Service.Endpoints.Admin;
using Xunit;

namespace Sendway.Service.Tests;

public class SharedCredentialEndpointsTests : IDisposable
{
    private const string AdminApiKey = "test-admin-key";

    private static readonly SolapiOptions Solapi = new() { ApiKey = "key", ApiSecret = "secret", From = "0212345678" };

    private readonly TestIsolatedStorage _storage = new();

    public void Dispose() => _storage.Dispose();

    // Every start shares one database and key directory, as a restarted deployment does. "Testing"
    // keeps a developer's local appsettings.Development.json out, so only `configured` is configured.
    private WebApplicationFactory<Program> Start(Dictionary<string, string?>? configured = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((context, configuration) =>
            {
                _storage.Apply(context, configuration);
                configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Sendway:AdminApiKey"] = AdminApiKey });
                configuration.AddInMemoryCollection(configured ?? []);
            });
        });

    private static HttpClient Admin(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Admin-Key", AdminApiKey);
        return client;
    }

    private static async Task<Dictionary<string, string?>> SourcesAsync(HttpClient admin) =>
        (await admin.GetFromJsonAsync<List<SharedCredentialResponse>>("/admin/credentials"))!
            .ToDictionary(credential => credential.Channel, credential => credential.Source);

    [Fact]
    public async Task Put_WhenConfigurationSaysNothing_SetsTheSharedValueAndItSurvivesARestart()
    {
        await using (var first = Start())
        {
            var admin = Admin(first);
            Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync("/admin/credentials/solapi", Solapi)).StatusCode);
            Assert.Equal("api", (await SourcesAsync(admin))["solapi"]);
        }

        await using var second = Start();

        var stored = await second.Services.GetRequiredService<ICredentialStore>().GetAsync<SolapiOptions>(null, ChannelCredentialNames.Solapi);
        Assert.Equal("0212345678", stored!.From);
        Assert.Equal("api", (await SourcesAsync(Admin(second)))["solapi"]);
    }

    [Fact]
    public async Task Put_WhenConfigurationSuppliesTheValue_Returns409()
    {
        await using var factory = Start(new Dictionary<string, string?>
        {
            ["Smtp:Host"] = "configured.example.com",
            ["Smtp:FromAddress"] = "sender@example.com"
        });
        var admin = Admin(factory);

        var put = await admin.PutAsJsonAsync("/admin/credentials/smtp", new SmtpOptions { Host = "api.example.com", FromAddress = "api@example.com" });

        Assert.Equal(HttpStatusCode.Conflict, put.StatusCode);
        var problem = await put.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        Assert.Equal("managed_by_configuration", problem!["code"].ToString());
        Assert.Equal("configured.example.com",
            (await factory.Services.GetRequiredService<ICredentialStore>().GetAsync<SmtpOptions>(null, ChannelCredentialNames.Smtp))!.Host);
        Assert.Equal("configuration", (await SourcesAsync(admin))["smtp"]);
    }

    [Fact]
    public async Task Get_ListsEveryShareableChannelWithItsSourceAndNoValues()
    {
        await using var factory = Start();

        var response = await Admin(factory).GetStringAsync("/admin/credentials");
        var sources = await SourcesAsync(Admin(factory));

        Assert.Equal(["smtp", "fcm", "solapi"], sources.Keys);
        Assert.All(sources.Values, Assert.Null);
        Assert.DoesNotContain("secret", response);
    }

    [Fact]
    public async Task Put_WithAnUnusableCredential_Returns400()
    {
        await using var factory = Start();

        var put = await Admin(factory).PutAsJsonAsync("/admin/credentials/solapi", new SolapiOptions { ApiKey = "key", ApiSecret = "", From = "0212345678" });

        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
    }

    [Theory]
    [InlineData("email-graph")]
    [InlineData("slack")]
    public async Task Put_ForAChannelWithoutASharedValue_Returns400(string channel)
    {
        await using var factory = Start();

        var put = await Admin(factory).PutAsJsonAsync($"/admin/credentials/{channel}", new { });

        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
    }

    [Fact]
    public async Task Delete_RemovesTheSharedValueButKeepsTenantOverrides()
    {
        await using var factory = Start();
        var admin = Admin(factory);
        var (tenantId, _) = await TestTenantSeeder.SeedAsync(factory);
        await admin.PutAsJsonAsync("/admin/credentials/solapi", Solapi);
        await admin.PutAsJsonAsync($"/admin/tenants/{tenantId}/credentials/solapi", new SolapiOptions { ApiKey = "k", ApiSecret = "s", From = "0311234567" });

        var delete = await admin.DeleteAsync("/admin/credentials/solapi");

        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        var store = factory.Services.GetRequiredService<ICredentialStore>();
        Assert.Null(await store.GetAsync<SolapiOptions>(null, ChannelCredentialNames.Solapi));
        Assert.Equal("0311234567", (await store.GetAsync<SolapiOptions>(tenantId, ChannelCredentialNames.Solapi))!.From);
        Assert.Null((await SourcesAsync(admin))["solapi"]);
    }

    [Fact]
    public async Task Requests_WithoutTheAdministratorKey_Return401()
    {
        await using var factory = Start();

        var response = await factory.CreateClient().PutAsJsonAsync("/admin/credentials/solapi", Solapi);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
