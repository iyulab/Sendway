namespace Sendway.Core;

public sealed class Tenant
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public required string ApiKeyHash { get; set; }

    // The key the tenant had before its last rotation, kept working until PreviousApiKeyExpiresAt
    // so the calling service can switch to the new key without an outage. Null when the rotation
    // ended the old key at once.
    public string? PreviousApiKeyHash { get; set; }

    public DateTimeOffset? PreviousApiKeyExpiresAt { get; set; }

    public required bool Active { get; set; }

    // A sandbox tenant's messages never leave the instance: DeliverySimulator answers in place of the
    // providers. For a calling service's development and test environments. Set at creation only.
    public bool Sandbox { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    // When the previous key stops working, or null when it no longer works at `now`.
    public DateTimeOffset? PreviousApiKeyExpiryAt(DateTimeOffset now) =>
        PreviousApiKeyHash is not null && PreviousApiKeyExpiresAt > now ? PreviousApiKeyExpiresAt : null;
}

// The tenant a presented key belongs to. ExpiresAt is set when it is the tenant's previous key,
// still working within its overlap.
public sealed record TenantKeyMatch(Tenant Tenant, DateTimeOffset? ExpiresAt);
