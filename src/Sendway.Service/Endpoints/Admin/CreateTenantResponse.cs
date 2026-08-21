namespace Sendway.Service.Endpoints.Admin;

public sealed record CreateTenantResponse(Guid TenantId, string Name, string ApiKey);
