namespace Sendway.Core;

public interface ICredentialStore
{
    Task<T?> GetAsync<T>(Guid? tenantId, string channel, CancellationToken cancellationToken = default) where T : class;

    Task SetAsync<T>(Guid? tenantId, string channel, T value, CancellationToken cancellationToken = default) where T : class;
}
