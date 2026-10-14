namespace Sendway.Service.Endpoints.Admin;

// The new key appears here only. PreviousKeyExpiresAt is when the key it replaced stops working —
// null when it already has.
public sealed record RotateTenantKeyResponse(string ApiKey, DateTimeOffset? PreviousKeyExpiresAt);

public sealed record RotateTenantKeyRequest(int? PreviousKeyValidForSeconds);
