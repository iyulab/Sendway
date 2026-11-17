using System.Net;

namespace Sendway.Service;

// Where Sendway's calls to SOLAPI leave from. SOLAPI can restrict an API key to a list of source
// addresses; a deployment whose own address is not fixed sends those calls through a proxy that is
// (an HTTP proxy that tunnels with CONNECT, or a SOCKS5 one). Without the setting the calls go out
// directly, as they always did.
//
// The address may carry the proxy's credentials (http://user:password@host:3128, characters in them
// percent-encoded). The credentials are handed to the proxy handler instead of being kept in the
// address, so no error text or log line built from the address can contain them.
public static class SolapiProxy
{
    public const string ConfigurationKey = "Sendway:Solapi:ProxyUrl";

    public static HttpMessageHandler CreateHandler(string? proxyUrl, ILogger? logger = null)
    {
        var handler = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) };
        if (string.IsNullOrWhiteSpace(proxyUrl))
        {
            return handler;
        }

        var (address, credential) = Parse(proxyUrl);
        handler.UseProxy = true;
        handler.Proxy = new WebProxy(address) { Credentials = credential };
        return new ProxyFailureHandler(handler, address.Authority, logger);
    }

    // The value is never echoed in the error: it may hold the proxy's password.
    private static (Uri Address, NetworkCredential? Credential) Parse(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "socks5")
            || string.IsNullOrEmpty(uri.Host)
            || uri.AbsolutePath != "/"
            || uri.Query.Length > 0
            || uri.Fragment.Length > 0)
        {
            throw new InvalidOperationException(
                $"{ConfigurationKey} is not a proxy address (expected http://[user:password@]host:port).");
        }

        NetworkCredential? credential = null;
        if (uri.UserInfo.Length > 0)
        {
            var parts = uri.UserInfo.Split(':', 2);
            credential = new NetworkCredential(
                Uri.UnescapeDataString(parts[0]),
                parts.Length == 2 ? Uri.UnescapeDataString(parts[1]) : "");
        }

        return (new UriBuilder(uri) { UserName = "", Password = "" }.Uri, credential);
    }

    // The text of a failure at the proxy names the proxy, and a message's error text is shown to the
    // tenant that sent it. The operator gets the full failure in the log; the message gets a plain line.
    private sealed class ProxyFailureHandler(HttpMessageHandler inner, string proxyAuthority, ILogger? logger)
        : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            try
            {
                return await base.SendAsync(request, cancellationToken);
            }
            catch (HttpRequestException ex) when (Mentions(ex))
            {
                logger?.LogWarning(ex, "A call to SOLAPI failed at the outgoing proxy.");
                throw new HttpRequestException("The request to the SMS provider failed at the outgoing proxy.");
            }
        }

        private bool Mentions(Exception? exception)
        {
            for (; exception is not null; exception = exception.InnerException)
            {
                if (exception.Message.Contains(proxyAuthority, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
