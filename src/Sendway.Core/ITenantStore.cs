namespace Sendway.Core;

public interface ITenantStore
{
    Task<Tenant> CreateAsync(string name, string apiKeyHash, bool sandbox = false, CancellationToken cancellationToken = default);

    Task<Tenant?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    // The tenant whose current key, or whose previous key still within its overlap at `now`, has
    // this hash.
    Task<TenantKeyMatch?> FindByApiKeyHashAsync(string apiKeyHash, DateTimeOffset now, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Tenant>> ListAsync(CancellationToken cancellationToken = default);

    // Replaces the tenant's own limits; a null value falls back to the deployment's default.
    Task<bool> SetLimitsAsync(Guid id, string? smsCountries, int? smsPerDay, int? emailPerDay, int? pushPerDay, CancellationToken cancellationToken = default);

    Task<bool> SetActiveAsync(Guid id, bool active, CancellationToken cancellationToken = default);

    // Gives the tenant a new key. With `previousKeyExpiresAt` the key it had keeps working until
    // then; without, it stops at once. Either way an older previous key ends now — there is one.
    Task<bool> RotateApiKeyAsync(Guid id, string apiKeyHash, DateTimeOffset? previousKeyExpiresAt, CancellationToken cancellationToken = default);

    // Ends the previous key's overlap now. False when the tenant has no previous key still working.
    Task<bool> RevokePreviousApiKeyAsync(Guid id, DateTimeOffset now, CancellationToken cancellationToken = default);
}
