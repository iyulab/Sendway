using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Sendway.Core.Tests;

// An in-process SMTP server for sender tests. Accepts every message after `delay` and records how many
// sessions were opened and how many were open at once. The local part of an address picks a refusal:
// a recipient starting with one of RecipientReplies' keys gets that reply to RCPT, a recipient starting
// with "spam" is accepted but the message is refused after DATA, and a sender starting with
// "badsender" is refused at MAIL; the password "wrong" is refused at AUTH. With closeAfterEachMessage
// it hangs up after every delivered message, the way a server that drops idle or used connections would.
internal sealed class TestSmtpServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly TimeSpan _delay;
    private readonly bool _closeAfterEachMessage;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _accepting;
    private int _open;
    private int _maxOpen;
    private int _opened;
    private int _delivered;

    private static readonly (string Prefix, string Reply)[] RecipientReplies =
    [
        ("reject", "550 5.1.1 no such user"),
        ("tempfail", "451 4.7.1 greylisted, try again later"),
        ("plainreject", "550 no such user"),
        ("mixed", "550 4.2.2 mailbox full"),
    ];

    public TestSmtpServer(TimeSpan delay = default, bool closeAfterEachMessage = false)
    {
        _delay = delay;
        _closeAfterEachMessage = closeAfterEachMessage;
        _listener.Start();
        _accepting = AcceptAsync();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public int MaxConcurrentSessions => Volatile.Read(ref _maxOpen);

    public int SessionsOpened => Volatile.Read(ref _opened);

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
        Interlocked.Increment(ref _opened);
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
                var refuseMessage = false;
                while (await reader.ReadLineAsync(_stop.Token) is { } line)
                {
                    if (inData)
                    {
                        if (line == ".")
                        {
                            inData = false;
                            if (_delay > TimeSpan.Zero)
                            {
                                await Task.Delay(_delay);
                            }

                            if (refuseMessage)
                            {
                                refuseMessage = false;
                                await writer.WriteLineAsync("554 5.7.1 message refused as spam");
                                continue;
                            }

                            Interlocked.Increment(ref _delivered);
                            await writer.WriteLineAsync("250 queued");
                            if (_closeAfterEachMessage)
                            {
                                break;
                            }
                        }

                        continue;
                    }

                    var verb = line.Split(' ')[0].ToUpperInvariant();
                    if (verb is "EHLO" or "HELO")
                    {
                        await writer.WriteLineAsync("250-test");
                        await writer.WriteLineAsync("250 AUTH PLAIN");
                    }
                    else if (verb == "AUTH" && IsWrongPassword(line))
                    {
                        await writer.WriteLineAsync("535 5.7.8 authentication credentials invalid");
                    }
                    else if (verb == "AUTH")
                    {
                        await writer.WriteLineAsync("235 2.7.0 accepted");
                    }
                    else if (verb == "MAIL" && line.Contains("<badsender", StringComparison.OrdinalIgnoreCase))
                    {
                        await writer.WriteLineAsync("550 5.7.1 sender not allowed");
                    }
                    else if (verb == "RCPT" && RecipientReply(line) is { } reply)
                    {
                        await writer.WriteLineAsync(reply);
                    }
                    else if (verb == "RCPT" && line.Contains("<spam", StringComparison.OrdinalIgnoreCase))
                    {
                        refuseMessage = true;
                        await writer.WriteLineAsync("250 ok");
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
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
        }
        finally
        {
            Interlocked.Decrement(ref _open);
        }
    }

    // AUTH PLAIN carries "\0user\0password" in base64 on the same line.
    private static bool IsWrongPassword(string line)
    {
        var parts = line.Split(' ');
        if (parts.Length < 3)
        {
            return false;
        }

        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(parts[2])).EndsWith("\0wrong", StringComparison.Ordinal);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string? RecipientReply(string line)
    {
        foreach (var (prefix, reply) in RecipientReplies)
        {
            if (line.Contains("<" + prefix, StringComparison.OrdinalIgnoreCase))
            {
                return reply;
            }
        }

        return null;
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        await _accepting;
    }
}
