using Sendway.Core;
using Xunit;

namespace Sendway.Core.Tests;

public class SmtpConnectionLimitTests
{
    [Theory]
    [InlineData(SmtpProvider.Office365, null, 3)]
    [InlineData(SmtpProvider.Gmail, null, null)]
    [InlineData(SmtpProvider.Office365, 1, 1)]
    public void MaxConnections_ComesFromTheCredential_OrTheProvidersKnownLimit(SmtpProvider provider, int? configured, int? expected)
    {
        var options = new SmtpOptions { Provider = provider, MaxConnections = configured, FromAddress = "sender@example.com" };

        Assert.Equal(expected, SmtpProviderPresets.MaxConnections(options));
    }

    [Theory]
    [InlineData("smtp.office365.com", 3)]
    [InlineData("SMTP.Office365.com", 3)]
    [InlineData("smtp.example.com", null)]
    public void MaxConnections_ForAnExplicitHost_FollowsTheHost(string host, int? expected)
    {
        var options = new SmtpOptions { Host = host, FromAddress = "sender@example.com" };

        Assert.Equal(expected, SmtpProviderPresets.MaxConnections(options));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validator_RefusesANonPositiveLimit(int value)
    {
        var options = new SmtpOptions { Host = "smtp.example.com", MaxConnections = value, FromAddress = "sender@example.com" };

        Assert.Equal("maxConnections must be at least 1.", ChannelCredentialValidator.Validate(options));
    }

    [Fact]
    public async Task SendAsync_NeverOpensMoreConnectionsToOneAccountThanItsLimit()
    {
        await using var server = new TestSmtpServer(TimeSpan.FromMilliseconds(300));
        var sender = new SmtpEmailSender(new SingleCredentialStore(ChannelCredentialNames.Smtp, new SmtpOptions
        {
            Host = "127.0.0.1", Port = server.Port, MaxConnections = 2, FromAddress = "sender@example.com"
        }));

        await Task.WhenAll(Enumerable.Range(0, 6).Select(i =>
            sender.SendAsync(new EmailMessage(Guid.NewGuid(), [$"to{i}@example.com"], "s", "b"))));

        Assert.Equal(6, server.Delivered);
        Assert.Equal(2, server.MaxConcurrentSessions);
    }

    [Fact]
    public async Task SendAsync_WithoutALimit_SendsInParallel()
    {
        await using var server = new TestSmtpServer(TimeSpan.FromMilliseconds(300));
        var sender = new SmtpEmailSender(new SingleCredentialStore(ChannelCredentialNames.Smtp, new SmtpOptions
        {
            Host = "127.0.0.1", Port = server.Port, FromAddress = "sender@example.com"
        }));

        await Task.WhenAll(Enumerable.Range(0, 4).Select(i =>
            sender.SendAsync(new EmailMessage(Guid.NewGuid(), [$"to{i}@example.com"], "s", "b"))));

        Assert.Equal(4, server.MaxConcurrentSessions);
    }

    [Fact]
    public async Task SendAsync_WaitingForAConnection_StopsWhenTheAttemptIsCancelled()
    {
        await using var server = new TestSmtpServer(TimeSpan.FromSeconds(2));
        var sender = new SmtpEmailSender(new SingleCredentialStore(ChannelCredentialNames.Smtp, new SmtpOptions
        {
            Host = "127.0.0.1", Port = server.Port, MaxConnections = 1, FromAddress = "sender@example.com"
        }));

        var first = sender.SendAsync(new EmailMessage(Guid.NewGuid(), ["a@example.com"], "s", "b"));
        await Task.Delay(100);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            sender.SendAsync(new EmailMessage(Guid.NewGuid(), ["b@example.com"], "s", "b"), cancellation.Token));
        await first;
        Assert.Equal(1, server.Delivered);
    }
}
