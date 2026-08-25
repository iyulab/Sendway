using Sendway.Core;
using Xunit;

namespace Sendway.Core.Tests;

public class SmtpEmailSenderValidationTests
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

    private static SmtpEmailSender CreateSender()
    {
        var credentialStore = new SingleCredentialStore(ChannelCredentialNames.Smtp, new SmtpOptions
        {
            Host = "localhost",
            Port = 2525,
            FromAddress = "sendway@localhost"
        });

        return new SmtpEmailSender(credentialStore);
    }
}
