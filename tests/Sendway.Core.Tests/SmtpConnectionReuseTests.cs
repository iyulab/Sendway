using Sendway.Core;
using Xunit;

namespace Sendway.Core.Tests;

public class SmtpConnectionReuseTests
{
    private readonly ManualTimeProvider _time = new();

    private SmtpEmailSender SenderFor(TestSmtpServer server, string? password = null, int? maxConnections = null) =>
        new(new SingleCredentialStore(ChannelCredentialNames.Smtp, new SmtpOptions
        {
            Host = "127.0.0.1",
            Port = server.Port,
            Username = password is null ? null : "user",
            Password = password,
            MaxConnections = maxConnections,
            FromAddress = "sender@example.com"
        }), _time);

    private static EmailMessage Message(string to = "user@example.com") => new(Guid.NewGuid(), [to], "s", "b");

    [Fact]
    public async Task ConsecutiveMessages_ShareOneConnection()
    {
        await using var server = new TestSmtpServer();
        using var sender = SenderFor(server);

        for (var i = 0; i < 5; i++)
        {
            await sender.SendAsync(Message());
        }

        Assert.Equal(5, server.Delivered);
        Assert.Equal(1, server.SessionsOpened);
    }

    [Fact]
    public async Task AConnectionTheServerClosed_IsReplacedWithinTheSameSend()
    {
        await using var server = new TestSmtpServer(closeAfterEachMessage: true);
        using var sender = SenderFor(server);

        for (var i = 0; i < 3; i++)
        {
            await sender.SendAsync(Message());
        }

        Assert.Equal(3, server.Delivered);
        Assert.Equal(3, server.SessionsOpened);
    }

    [Fact]
    public async Task AConnectionIdleTooLong_IsNotReused()
    {
        await using var server = new TestSmtpServer();
        using var sender = SenderFor(server);

        await sender.SendAsync(Message());
        _time.Advance(SmtpConnectionPool.IdleTimeout);
        await sender.SendAsync(Message());

        Assert.Equal(2, server.SessionsOpened);
    }

    [Fact]
    public async Task AConnectionThatFailed_IsNotReused()
    {
        await using var server = new TestSmtpServer();
        using var sender = SenderFor(server);

        await sender.SendAsync(Message());
        await Assert.ThrowsAsync<InvalidRecipientException>(() => sender.SendAsync(Message("reject@example.com")));
        await sender.SendAsync(Message());

        Assert.Equal(2, server.Delivered);
        Assert.Equal(2, server.SessionsOpened);
    }

    [Fact]
    public async Task AChangedPassword_GetsANewConnection()
    {
        await using var server = new TestSmtpServer();
        var store = new ChangingCredentialStore(Credential(server, "old"));
        using var sender = new SmtpEmailSender(store, _time);

        await sender.SendAsync(Message());
        await sender.SendAsync(Message());
        store.Value = Credential(server, "new");
        await sender.SendAsync(Message());

        Assert.Equal(3, server.Delivered);
        Assert.Equal(2, server.SessionsOpened);
    }

    private static SmtpOptions Credential(TestSmtpServer server, string password) => new()
    {
        Host = "127.0.0.1", Port = server.Port, Username = "user", Password = password, FromAddress = "sender@example.com"
    };

    private sealed class ChangingCredentialStore(SmtpOptions value) : ICredentialStore
    {
        public SmtpOptions Value { get; set; } = value;

        public Task<T?> GetAsync<T>(Guid? tenantId, string channel, CancellationToken cancellationToken = default) where T : class =>
            Task.FromResult(Value as T);

        public Task SetAsync<T>(Guid? tenantId, string channel, T value, CancellationToken cancellationToken = default) where T : class =>
            throw new NotSupportedException();

        public Task DeleteAsync(Guid? tenantId, string channel, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    [Fact]
    public async Task KeptConnections_StayWithinTheAccountLimit()
    {
        await using var server = new TestSmtpServer(TimeSpan.FromMilliseconds(200));
        using var sender = SenderFor(server, maxConnections: 2);

        for (var round = 0; round < 3; round++)
        {
            await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => sender.SendAsync(Message())));
        }

        Assert.Equal(15, server.Delivered);
        Assert.Equal(2, server.MaxConcurrentSessions);
        Assert.Equal(2, server.SessionsOpened);
    }
}
