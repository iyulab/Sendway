using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Sendway.Core.Tests;

// An in-process SMTP server for sender tests. Accepts every message after `delay`, refuses recipients
// whose address starts with "reject", and records how many sessions were opened and how many were
// open at once. With closeAfterEachMessage it hangs up after every delivered message, the way a
// server that drops idle or used connections would.
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
                    else if (verb == "AUTH")
                    {
                        await writer.WriteLineAsync("235 2.7.0 accepted");
                    }
                    else if (verb == "RCPT" && line.Contains("<reject", StringComparison.OrdinalIgnoreCase))
                    {
                        await writer.WriteLineAsync("550 5.1.1 no such user");
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

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        await _accepting;
    }
}
