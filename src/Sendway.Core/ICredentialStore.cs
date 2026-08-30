namespace Sendway.Core;

public interface ICredentialStore
{
    Task<T?> GetAsync<T>(Guid? tenantId, string channel, CancellationToken cancellationToken = default) where T : class;

    Task SetAsync<T>(Guid? tenantId, string channel, T value, CancellationToken cancellationToken = default) where T : class;

    // Removes a tenant's override, falling back to the shared default (or "unconfigured" if none
    // exists) — the only way to revert a tenant off a channel once PUT has set one. Deleting the
    // shared default itself (tenantId: null) is out of scope: no caller needs it, and it would be a
    // far more destructive action than reverting a single tenant's override.
    Task DeleteAsync(Guid tenantId, string channel, CancellationToken cancellationToken = default);
}
