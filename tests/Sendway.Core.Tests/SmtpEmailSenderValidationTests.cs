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
    public async Task SendAsync_WithMalformedRecipient_ThrowsInvalidRecipientException(string to)
    {
        var credentialStore = new SingleCredentialStore(ChannelCredentialNames.Smtp, new SmtpOptions
        {
            Host = "localhost",
            Port = 2525,
            FromAddress = "sendway@localhost"
        });
        var sender = new SmtpEmailSender(credentialStore);
        var message = new EmailMessage(Guid.NewGuid(), to, "Subject", "Body");

        await Assert.ThrowsAsync<InvalidRecipientException>(() => sender.SendAsync(message));
    }
}
