namespace Sendway.Core;

public interface ICredentialStore
{
    Task<T?> GetAsync<T>(Guid? tenantId, string channel, CancellationToken cancellationToken = default) where T : class;

    Task SetAsync<T>(Guid? tenantId, string channel, T value, CancellationToken cancellationToken = default) where T : class;

    // Removes a tenant's override, so the tenant falls back to the shared default (or to
    // "unconfigured" if there is none). With tenantId null it removes the shared default itself,
    // which leaves every tenant without an override unconfigured on that channel.
    Task DeleteAsync(Guid? tenantId, string channel, CancellationToken cancellationToken = default);
}
