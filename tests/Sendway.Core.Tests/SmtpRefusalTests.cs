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

    // One mistyped address must not keep the others from getting the message: the recipients refused for
    // good are left out and named, the rest receive it.
    [Fact]
    public async Task ARecipientRefusedForGoodAmongSeveral_IsLeftOutAndTheOthersReceiveIt()
    {
        await using var server = new TestSmtpServer();
        using var sender = SenderFor(server);

        var delivery = await sender.SendAsync(Message("user@example.com", "reject@example.com", "other@example.com"));

        var refused = Assert.Single(delivery.RefusedRecipients);
        Assert.Equal("reject@example.com", refused.Address);
        Assert.Contains("5.1.1", refused.Error);
        Assert.Equal(1, server.Delivered);
    }

    // A temporary refusal abandons the message before its content is sent, so nobody has it and retrying
    // it whole sends no one a second copy.
    [Fact]
    public async Task ARecipientRefusedForNowAmongSeveral_LeavesTheWholeMessageToBeRetried()
    {
        await using var server = new TestSmtpServer();
        using var sender = SenderFor(server);

        await Assert.ThrowsAsync<SmtpCommandException>(() => sender.SendAsync(Message("user@example.com", "tempfail@example.com")));

        Assert.Equal(0, server.Delivered);
    }

    [Fact]
    public async Task EveryRecipientRefusedForGood_EndsTheMessageNamingThemAll()
    {
        await using var server = new TestSmtpServer();
        using var sender = SenderFor(server);

        var exception = await Assert.ThrowsAsync<InvalidRecipientException>(() =>
            sender.SendAsync(Message("reject@example.com", "plainreject@example.com")));

        Assert.Contains("reject@example.com", exception.Message);
        Assert.Contains("plainreject@example.com", exception.Message);
        Assert.Equal(0, server.Delivered);
    }

    // A kept connection carries no refusals from the message it sent before.
    [Fact]
    public async Task TheNextMessageOnAKeptConnection_StartsWithNoRefusals()
    {
        await using var server = new TestSmtpServer();
        using var sender = SenderFor(server);

        await sender.SendAsync(Message("user@example.com", "reject@example.com"));
        var next = await sender.SendAsync(Message("user@example.com"));

        Assert.Empty(next.RefusedRecipients);
        Assert.Equal(1, server.SessionsOpened);
    }
}
