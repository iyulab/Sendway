namespace Sendway.Core;

public interface ICredentialStore
{
    Task<T?> GetAsync<T>(string channel, CancellationToken cancellationToken = default) where T : class;

    Task SetAsync<T>(string channel, T value, CancellationToken cancellationToken = default) where T : class;
}
