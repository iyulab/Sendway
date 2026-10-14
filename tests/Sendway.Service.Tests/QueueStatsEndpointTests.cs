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

public class QueueStatsEndpointTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private const string AdminApiKey = "test-admin-key";

    private readonly WebApplicationFactory<Program> _factory;
    private readonly TestIsolatedStorage _storage = new();

    public QueueStatsEndpointTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((context, configuration) =>
            {
                _storage.Apply(context, configuration);
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Sendway:AdminApiKey"] = AdminApiKey
                });
            });
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IEmailSender>();
                services.AddSingleton<IEmailSender, NoopEmailSender>();
            });
        });
    }

    public void Dispose() => _storage.Dispose();

    private HttpClient CreateAdminClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Admin-Key", AdminApiKey);
        return client;
    }

    [Fact]
    public async Task Get_ReportsWhatIsWaitingAndThenThatItDrained()
    {
        var (_, apiKey) = await TestTenantSeeder.SeedAsync(_factory);
        var tenant = _factory.CreateClient();
        tenant.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        await tenant.PostAsJsonAsync("/messages/email", new SendEmailRequest(["user@example.com"], "Subject", "Body"));
        await tenant.PostAsJsonAsync("/messages/email", new SendEmailRequest(["user@example.com"], "Subject", "Body"));
        var admin = CreateAdminClient();

        var waiting = await admin.GetFromJsonAsync<QueuesResponse>("/admin/queues");

        Assert.Equal(2, waiting!.Messages.Queued);
        Assert.Equal(0, waiting.Messages.Sending);
        Assert.NotNull(waiting.Messages.OldestDueSeconds);
        Assert.Equal(0, waiting.Webhooks.Queued);

        await TestDelivery.DeliverPendingAsync(_factory);
        var drained = await admin.GetFromJsonAsync<QueuesResponse>("/admin/queues");

        Assert.Equal(0, drained!.Messages.Queued);
        Assert.Null(drained.Messages.OldestDueSeconds);
        Assert.Equal(0, drained.Messages.FailedInLast24Hours);
    }

    [Fact]
    public async Task Get_WithoutTheAdminKey_Returns401()
    {
        var response = await _factory.CreateClient().GetAsync("/admin/queues");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private sealed class NoopEmailSender : IEmailSender
    {
        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
