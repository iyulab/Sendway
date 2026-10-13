using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sendway.Core;
using Xunit;

namespace Sendway.Service.Tests;

public class ChannelCredentialSeederTests : IDisposable
{
    private readonly TestIsolatedStorage _storage = new();

    public void Dispose() => _storage.Dispose();

    [Fact]
    public async Task Start_ConfiguredSmtpChangedSinceLastStart_SharedCredentialFollowsConfiguration()
    {
        await using (var first = StartWithSmtpHost("first.example.com"))
        {
            var stored = await SharedSmtpAsync(first);
            Assert.Equal("first.example.com", stored!.Host);
        }

        await using var second = StartWithSmtpHost("second.example.com");

        var afterRestart = await SharedSmtpAsync(second);
        Assert.Equal("second.example.com", afterRestart!.Host);
    }

    // Both starts share one database and key directory, as a restarted deployment does.
    private WebApplicationFactory<Program> StartWithSmtpHost(string host) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((context, configuration) =>
            {
                _storage.Apply(context, configuration);
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Smtp:Host"] = host,
                    ["Smtp:FromAddress"] = "sender@example.com"
                });
            });
        });

    private static Task<SmtpOptions?> SharedSmtpAsync(WebApplicationFactory<Program> factory) =>
        factory.Services.GetRequiredService<ICredentialStore>().GetAsync<SmtpOptions>(null, ChannelCredentialNames.Smtp);
}
