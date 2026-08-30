using Sendway.Core;
using Xunit;

namespace Sendway.Core.Tests;

public class GraphEmailSenderTests
{
    [Theory]
    [InlineData("not-an-email-address")]
    [InlineData("plainaddress")]
    [InlineData("user@")]
    [InlineData("@example.com")]
    public async Task SendAsync_WithMalformedToRecipient_ThrowsInvalidRecipientException(string to)
    {
        var sender = CreateSender();
        var message = new EmailMessage(Guid.NewGuid(), [to], "Subject", "Body");

        await Assert.ThrowsAsync<InvalidRecipientException>(() => sender.SendAsync(message));
    }

    [Fact]
    public async Task SendAsync_WithMalformedCcRecipient_ThrowsInvalidRecipientException()
    {
        var sender = CreateSender();
        var message = new EmailMessage(Guid.NewGuid(), ["user@example.com"], "Subject", "Body", cc: ["not-an-email-address"]);

        await Assert.ThrowsAsync<InvalidRecipientException>(() => sender.SendAsync(message));
    }

    [Fact]
    public async Task SendAsync_WithMalformedBccRecipient_ThrowsInvalidRecipientException()
    {
        var sender = CreateSender();
        var message = new EmailMessage(Guid.NewGuid(), ["user@example.com"], "Subject", "Body", bcc: ["not-an-email-address"]);

        await Assert.ThrowsAsync<InvalidRecipientException>(() => sender.SendAsync(message));
    }

    // Constructing ClientSecretCredential/GraphServiceClient performs no I/O — the malformed-address
    // check runs (and throws) before the first actual network call, so these fake, never-valid
    // credentials are enough to exercise the pre-flight validation path without a live Azure AD app.
    private static GraphEmailSender CreateSender()
    {
        var credentialStore = new SingleCredentialStore(ChannelCredentialNames.EmailGraph, new GraphOptions
        {
            DirectoryId = "00000000-0000-0000-0000-000000000000",
            ClientId = "00000000-0000-0000-0000-000000000000",
            ClientSecret = "not-a-real-secret",
            FromAddress = "sendway@localhost"
        });

        return new GraphEmailSender(credentialStore);
    }
}
