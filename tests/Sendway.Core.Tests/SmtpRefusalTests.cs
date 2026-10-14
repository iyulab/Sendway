using MailKit.Net.Smtp;
using Sendway.Core;
using Xunit;

namespace Sendway.Core.Tests;

// What the SMTP sender throws decides whether the delivery processor retries: InvalidRecipientException
// and MessageRejectedException end the message, anything else is tried again.
public class SmtpRefusalTests
{
    private static SmtpEmailSender SenderFor(TestSmtpServer server, string from = "sender@example.com", string? password = null) =>
        new(new SingleCredentialStore(ChannelCredentialNames.Smtp, new SmtpOptions
        {
            Host = "127.0.0.1",
            Port = server.Port,
            Username = password is null ? null : "user",
            Password = password,
            FromAddress = from
        }));

    private static EmailMessage Message(params string[] to) => new(Guid.NewGuid(), to, "s", "b");

    [Theory]
    [InlineData("reject@example.com", "5.1.1")]        // 550 with an enhanced code
    [InlineData("plainreject@example.com", "no such user")] // 550 without one: the basic code decides
    public async Task APermanentlyRefusedRecipient_EndsTheMessage(string recipient, string expectedInError)
    {
        await using var server = new TestSmtpServer();
        using var sender = SenderFor(server);

        var exception = await Assert.ThrowsAsync<InvalidRecipientException>(() => sender.SendAsync(Message(recipient)));

        Assert.Contains("refused the recipient", exception.Message);
        Assert.Contains(expectedInError, exception.Message);
    }

    [Theory]
    [InlineData("tempfail@example.com")]               // 451 4.7.1
    [InlineData("mixed@example.com")]                  // 550 4.2.2: the enhanced code says temporary
    public async Task ATemporarilyRefusedRecipient_IsLeftToBeRetried(string recipient)
    {
        await using var server = new TestSmtpServer();
        using var sender = SenderFor(server);

        await Assert.ThrowsAsync<SmtpCommandException>(() => sender.SendAsync(Message(recipient)));
    }

    [Fact]
    public async Task AMessageRefusedAfterItsContent_EndsTheMessage()
    {
        await using var server = new TestSmtpServer();
        using var sender = SenderFor(server);

        var exception = await Assert.ThrowsAsync<MessageRejectedException>(() => sender.SendAsync(Message("spam@example.com")));

        Assert.Contains("5.7.1", exception.Message);
    }

    // A refused sender address is a setting an operator can correct, like the other channels' sender
    // refusals, so the message stays retried while they do.
    [Fact]
    public async Task ARefusedSender_IsLeftToBeRetried()
    {
        await using var server = new TestSmtpServer();
        using var sender = SenderFor(server, from: "badsender@example.com");

        var exception = await Assert.ThrowsAsync<SmtpCommandException>(() => sender.SendAsync(Message("user@example.com")));

        Assert.Equal(SmtpErrorCode.SenderNotAccepted, exception.ErrorCode);
    }

    // A refused login is the account's problem, not the message's: an operator fixes the credential and
    // the waiting messages go out on their next attempt.
    [Fact]
    public async Task ARefusedLogin_IsLeftToBeRetried()
    {
        await using var server = new TestSmtpServer();
        using var sender = SenderFor(server, password: "wrong");

        await Assert.ThrowsAsync<MailKit.Security.AuthenticationException>(() => sender.SendAsync(Message("user@example.com")));
    }

    // The recipients of one message share one transaction: one refused address ends it for all of them.
    [Fact]
    public async Task OneRefusedRecipientAmongSeveral_EndsTheWholeMessage()
    {
        await using var server = new TestSmtpServer();
        using var sender = SenderFor(server);

        await Assert.ThrowsAsync<InvalidRecipientException>(() => sender.SendAsync(Message("user@example.com", "reject@example.com")));

        Assert.Equal(0, server.Delivered);
    }
}
