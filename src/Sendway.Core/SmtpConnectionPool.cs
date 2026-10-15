namespace Sendway.Core;

// Connections that have just delivered a message, kept open for the next message to the same account
// so it skips connecting, TLS and authentication (well over a second per message to a typical
// provider). Only a connection that succeeded is kept, so one that failed is never handed out again.
internal sealed class SmtpConnectionPool : IDisposable
{
    // Well under the five minutes RFC 5321 lets a server wait before closing an idle session, so a
    // kept connection is rarely found closed when it is taken again.
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(30);

    private readonly TimeProvider _timeProvider;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, List<(TrackingSmtpClient Client, DateTimeOffset IdleSince)>> _idle = new(StringComparer.Ordinal);
    private bool _disposed;

    public SmtpConnectionPool(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    // The most recently kept open connection to this account, or null. Connections idle for too long
    // — to any account — are closed on the way.
    public TrackingSmtpClient? TryTake(string account)
    {
        List<TrackingSmtpClient> expired;
        TrackingSmtpClient? taken = null;

        lock (_gate)
        {
            expired = RemoveExpired(_timeProvider.GetUtcNow());

            if (_idle.TryGetValue(account, out var connections))
            {
                while (taken is null && connections.Count > 0)
                {
                    var (client, _) = connections[^1];
                    connections.RemoveAt(connections.Count - 1);

                    if (client.IsConnected)
                    {
                        taken = client;
                    }
                    else
                    {
                        expired.Add(client);
                    }
                }
            }
        }

        foreach (var client in expired)
        {
            client.Dispose();
        }

        return taken;
    }

    public void Return(string account, TrackingSmtpClient client)
    {
        lock (_gate)
        {
            if (!_disposed && client.IsConnected)
            {
                if (!_idle.TryGetValue(account, out var connections))
                {
                    _idle[account] = connections = [];
                }

                connections.Add((client, _timeProvider.GetUtcNow()));
                return;
            }
        }

        client.Dispose();
    }

    public void Dispose()
    {
        List<TrackingSmtpClient> all;
        lock (_gate)
        {
            _disposed = true;
            all = _idle.Values.SelectMany(connections => connections.Select(c => c.Client)).ToList();
            _idle.Clear();
        }

        foreach (var client in all)
        {
            client.Dispose();
        }
    }

    private List<TrackingSmtpClient> RemoveExpired(DateTimeOffset now)
    {
        var expired = new List<TrackingSmtpClient>();
        foreach (var (account, connections) in _idle.ToList())
        {
            expired.AddRange(connections.Where(c => now - c.IdleSince >= IdleTimeout).Select(c => c.Client));
            connections.RemoveAll(c => now - c.IdleSince >= IdleTimeout);
            if (connections.Count == 0)
            {
                _idle.Remove(account);
            }
        }

        return expired;
    }
}
