using System.Net;
using System.Net.Sockets;
using System.Text;
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
        await using var server = new RecordingSmtpServer(TimeSpan.FromMilliseconds(300));
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
        await using var server = new RecordingSmtpServer(TimeSpan.FromMilliseconds(300));
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
        await using var server = new RecordingSmtpServer(TimeSpan.FromSeconds(2));
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

    // Accepts every message after `delay`, and records how many sessions were open at once.
    private sealed class RecordingSmtpServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly TimeSpan _delay;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _accepting;
        private int _open;
        private int _maxOpen;
        private int _delivered;

        public RecordingSmtpServer(TimeSpan delay)
        {
            _delay = delay;
            _listener.Start();
            _accepting = AcceptAsync();
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public int MaxConcurrentSessions => Volatile.Read(ref _maxOpen);

        public int Delivered => Volatile.Read(ref _delivered);

        private async Task AcceptAsync()
        {
            try
            {
                while (true)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    _ = ServeAsync(client);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            var open = Interlocked.Increment(ref _open);
            int seen;
            while ((seen = Volatile.Read(ref _maxOpen)) < open && Interlocked.CompareExchange(ref _maxOpen, open, seen) != seen)
            {
            }

            try
            {
                using (client)
                {
                    var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII);
                    await using var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
                    await writer.WriteLineAsync("220 test");
                    var inData = false;
                    while (await reader.ReadLineAsync() is { } line)
                    {
                        if (inData)
                        {
                            if (line == ".")
                            {
                                inData = false;
                                await Task.Delay(_delay);
                                Interlocked.Increment(ref _delivered);
                                await writer.WriteLineAsync("250 queued");
                            }

                            continue;
                        }

                        var verb = line.Split(' ')[0].ToUpperInvariant();
                        if (verb is "EHLO" or "HELO")
                        {
                            await writer.WriteLineAsync("250 test");
                        }
                        else if (verb == "DATA")
                        {
                            inData = true;
                            await writer.WriteLineAsync("354 go ahead");
                        }
                        else if (verb == "QUIT")
                        {
                            await writer.WriteLineAsync("221 bye");
                            break;
                        }
                        else
                        {
                            await writer.WriteLineAsync("250 ok");
                        }
                    }
                }
            }
            catch (IOException)
            {
            }
            finally
            {
                Interlocked.Decrement(ref _open);
            }
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            await _accepting;
        }
    }
}
