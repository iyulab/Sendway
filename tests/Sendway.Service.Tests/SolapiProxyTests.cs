using System.Net;
using System.Net.Sockets;
using System.Text;
using Sendway.Service;
using Xunit;

namespace Sendway.Service.Tests;

public class SolapiProxyTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateHandler_WithoutAProxy_GoesOutDirectly(string? value)
    {
        using var handler = SolapiProxy.CreateHandler(value);

        var direct = Assert.IsType<SocketsHttpHandler>(handler);
        Assert.Null(direct.Proxy);
    }

    [Theory]
    [InlineData("banana")]
    [InlineData("ftp://user:s3cret@proxy.test:21")]
    [InlineData("http://user:s3cret@")]
    [InlineData("http://user:s3cret@proxy.test:3128/path")]
    [InlineData("http://user:s3cret@proxy.test:3128/?a=b")]
    public void CreateHandler_WithAnUnusableAddress_RefusesWithoutEchoingIt(string value)
    {
        var failure = Assert.Throws<InvalidOperationException>(() => SolapiProxy.CreateHandler(value));

        Assert.Contains(SolapiProxy.ConfigurationKey, failure.Message);
        Assert.DoesNotContain("s3cret", failure.Message);
        Assert.DoesNotContain(value, failure.Message);
    }

    [Fact]
    public async Task Send_ThroughTheProxy_TunnelsToTheHostWithTheProxyCredentials()
    {
        using var proxy = new FakeProxy("HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        using var client = new HttpClient(SolapiProxy.CreateHandler($"http://sendway:p%40ss%3Aword@127.0.0.1:{proxy.Port}"));

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("https://api.solapi.test/"));

        var request = await proxy.Request;
        Assert.StartsWith("CONNECT api.solapi.test:443 ", request);
        var expected = Convert.ToBase64String(Encoding.UTF8.GetBytes("sendway:p@ss:word"));
        Assert.Contains($"Proxy-Authorization: Basic {expected}", request, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Send_WhenTheProxyRefuses_TheFailureNamesNeitherTheProxyNorItsCredentials()
    {
        using var proxy = new FakeProxy("HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        using var client = new HttpClient(SolapiProxy.CreateHandler($"http://sendway:s3cret@127.0.0.1:{proxy.Port}"));

        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("https://api.solapi.test/"));

        Assert.Equal("The request to the SMS provider failed at the outgoing proxy.", failure.Message);
        Assert.Null(failure.InnerException);
    }

    [Fact]
    public async Task Send_WhenTheProxyCannotBeReached_TheFailureNamesNeitherTheProxyNorItsCredentials()
    {
        var port = FreePort();
        using var client = new HttpClient(SolapiProxy.CreateHandler($"http://sendway:s3cret@127.0.0.1:{port}"));

        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("https://api.solapi.test/"));

        Assert.DoesNotContain("127.0.0.1", failure.ToString());
        Assert.DoesNotContain("s3cret", failure.ToString());
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    // A proxy that asks for credentials first (407 with a Basic challenge, as real proxies do) and, once a
    // request carries them, keeps that request and answers with a fixed response.
    private sealed class FakeProxy : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly string _response;
        private readonly TaskCompletionSource<string> _authorized = new();

        public FakeProxy(string response)
        {
            _response = response;
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = AcceptAsync();
        }

        public int Port { get; }

        public Task<string> Request => _authorized.Task;

        private async Task AcceptAsync()
        {
            try
            {
                while (true)
                {
                    _ = ServeAsync(await _listener.AcceptTcpClientAsync());
                }
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException or InvalidOperationException)
            {
                // The listener was stopped.
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using var _ = client;
            var stream = client.GetStream();
            var received = new StringBuilder();
            var buffer = new byte[4096];
            while (true)
            {
                var end = received.ToString().IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (end < 0)
                {
                    int read;
                    try
                    {
                        read = await stream.ReadAsync(buffer);
                    }
                    catch (IOException)
                    {
                        return;
                    }

                    if (read == 0)
                    {
                        return;
                    }

                    received.Append(Encoding.ASCII.GetString(buffer, 0, read));
                    continue;
                }

                var head = received.ToString(0, end + 4);
                received.Remove(0, end + 4);
                if (head.Contains("Proxy-Authorization:", StringComparison.OrdinalIgnoreCase))
                {
                    _authorized.TrySetResult(head);
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(_response));
                    return;
                }

                await stream.WriteAsync(Encoding.ASCII.GetBytes(
                    "HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: Basic realm=\"proxy\"\r\nContent-Length: 0\r\n\r\n"));
            }
        }

        public void Dispose() => _listener.Stop();
    }
}
