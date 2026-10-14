using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sendway.Core;
using Sendway.Service.Endpoints;
using Xunit;

namespace Sendway.Service.Tests;

// The one place the background worker runs for real: every other test class drives delivery by
// hand, so this is what shows an accepted message actually gets delivered with nobody pushing it.
public class MessageDeliveryWorkerTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly RecordingEmailSender _sender = new();
    private readonly TestIsolatedStorage _storage = new();

    public MessageDeliveryWorkerTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((context, configuration) =>
            {
                _storage.Apply(context, configuration);
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Sendway:Delivery:WorkerEnabled"] = "true"
                });
            });
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IEmailSender>();
                services.AddSingleton<IEmailSender>(_sender);
            });
        });
    }

    public void Dispose() => _storage.Dispose();

    [Fact]
    public async Task AcceptedMessage_IsDeliveredInTheBackground()
    {
        var (_, apiKey) = await TestTenantSeeder.SeedAsync(_factory);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);

        var response = await client.PostAsJsonAsync(
            "/messages/email",
            new SendEmailRequest(["user@example.com"], "Subject", "Body"));
        var accepted = await response.Content.ReadFromJsonAsync<SendMessageResponse>();

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        await _sender.Delivered.WaitAsync(TimeSpan.FromSeconds(30));

        var status = await WaitForStatusAsync(client, accepted!.Id, "Sent");
        Assert.Equal(1, status.Attempts);
        Assert.NotNull(status.CompletedAt);
    }

    // The sender returning and the outcome being written are two steps; this bridges the gap
    // between them rather than assuming the write has landed.
    private static async Task<MessageStatusResponse> WaitForStatusAsync(HttpClient client, Guid id, string expected)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        MessageStatusResponse status;

        do
        {
            status = (await client.GetFromJsonAsync<MessageStatusResponse>($"/messages/{id}"))!;
            if (status.Status == expected)
            {
                return status;
            }

            await Task.Delay(50);
        }
        while (DateTimeOffset.UtcNow < deadline);

        Assert.Equal(expected, status.Status);
        return status;
    }

    private sealed class RecordingEmailSender : IEmailSender
    {
        private readonly TaskCompletionSource _delivered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Delivered => _delivered.Task;

        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
        {
            _delivered.TrySetResult();
            return Task.CompletedTask;
        }
    }
}
