using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Sendway.Service.Tests;

public class OpenApiDocumentTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly TestIsolatedStorage _storage = new();

    public OpenApiDocumentTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((context, configuration) => _storage.Apply(context, configuration));
        });
    }

    public void Dispose() => _storage.Dispose();

    [Fact]
    public async Task Get_WithoutAKey_ServesTheDescriptionCommittedToTheRepository()
    {
        var response = await _factory.CreateClient().GetAsync("/openapi/v1.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var served = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        var committed = JsonNode.Parse(await File.ReadAllTextAsync(CommittedDescriptionPath()));
        Assert.True(JsonNode.DeepEquals(committed, served), "docs/openapi.json is out of date: build the service to regenerate it.");
    }

    [Fact]
    public async Task Get_DescribesTheCallerApiOnly()
    {
        var document = JsonNode.Parse(await _factory.CreateClient().GetStringAsync("/openapi/v1.json"))!;

        var paths = document["paths"]!.AsObject().Select(p => p.Key).ToList();
        Assert.Equal(["/messages/email", "/messages/push", "/messages/sms", "/messages/{id}"], paths);
        Assert.Equal("X-Api-Key", document["components"]!["securitySchemes"]!["tenantKey"]!["name"]!.GetValue<string>());
        Assert.NotNull(document["webhooks"]!["messageOutcome"]);
    }

    private static string CommittedDescriptionPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Sendway.slnx")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(directory?.FullName ?? throw new InvalidOperationException("Repository root not found."), "docs", "openapi.json");
    }
}
