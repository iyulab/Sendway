using Sendway.Core;
using Xunit;

namespace Sendway.Core.Tests;

public class EmailSenderRouterTests
{
    [Fact]
    public async Task SendAsync_WithNoGraphCredentialConfigured_RoutesToSmtp()
    {
        var smtp = new FakeEmailSender();
        var graph = new FakeEmailSender();
        // Keyed to a channel other than email-graph, so GetAsync<GraphOptions>(..., "email-graph")
        // resolves to null — the "no override configured" case.
        var credentialStore = new SingleCredentialStore(ChannelCredentialNames.Smtp, new SmtpOptions { FromAddress = "sendway@localhost" });
        var router = new EmailSenderRouter(credentialStore, smtp, graph);
        var message = new EmailMessage(Guid.NewGuid(), ["user@example.com"], "Subject", "Body");

        await router.SendAsync(message);

        Assert.Same(message, smtp.LastMessage);
        Assert.Null(graph.LastMessage);
    }

    [Fact]
    public async Task SendAsync_WithGraphCredentialConfigured_RoutesToGraph()
    {
        var smtp = new FakeEmailSender();
        var graph = new FakeEmailSender();
        var credentialStore = new SingleCredentialStore(ChannelCredentialNames.EmailGraph, new GraphOptions
        {
            DirectoryId = "00000000-0000-0000-0000-000000000000",
            ClientId = "00000000-0000-0000-0000-000000000000",
            ClientSecret = "not-a-real-secret",
            FromAddress = "sendway@localhost"
        });
        var router = new EmailSenderRouter(credentialStore, smtp, graph);
        var message = new EmailMessage(Guid.NewGuid(), ["user@example.com"], "Subject", "Body");

        await router.SendAsync(message);

        Assert.Same(message, graph.LastMessage);
        Assert.Null(smtp.LastMessage);
    }

    private sealed class FakeEmailSender : IEmailSender
    {
        public EmailMessage? LastMessage { get; private set; }

        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
        {
            LastMessage = message;
            return Task.CompletedTask;
        }
    }
}
