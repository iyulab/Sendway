namespace Sendway.Core;

public interface ITenantStore
{
    Task<Tenant> CreateAsync(string name, string apiKeyHash, CancellationToken cancellationToken = default);

    Task<Tenant?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<Tenant?> GetByApiKeyHashAsync(string apiKeyHash, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Tenant>> ListAsync(CancellationToken cancellationToken = default);

    Task<bool> SetActiveAsync(Guid id, bool active, CancellationToken cancellationToken = default);

    Task<bool> SetApiKeyHashAsync(Guid id, string apiKeyHash, CancellationToken cancellationToken = default);
}
