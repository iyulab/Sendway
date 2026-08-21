using Sendway.Core;

namespace Sendway.Core.Tests;

internal sealed class SingleCredentialStore : ICredentialStore
{
    private readonly string _channel;
    private readonly object _value;

    public SingleCredentialStore(string channel, object value)
    {
        _channel = channel;
        _value = value;
    }

    public Task<T?> GetAsync<T>(Guid? tenantId, string channel, CancellationToken cancellationToken = default) where T : class
    {
        return Task.FromResult(channel == _channel ? _value as T : null);
    }

    public Task SetAsync<T>(Guid? tenantId, string channel, T value, CancellationToken cancellationToken = default) where T : class
    {
        throw new NotSupportedException();
    }
}
