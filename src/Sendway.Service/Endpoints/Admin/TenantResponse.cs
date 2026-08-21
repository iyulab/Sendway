namespace Sendway.Service.Endpoints.Admin;

public sealed record TenantResponse(Guid Id, string Name, bool Active, DateTimeOffset CreatedAt);
