namespace Sendway.Service.Endpoints.Admin;

// PreviousKeyExpiresAt is set while the key replaced by the last rotation still works.
public sealed record TenantResponse(Guid Id, string Name, bool Active, bool Sandbox, DateTimeOffset CreatedAt, DateTimeOffset? PreviousKeyExpiresAt);
