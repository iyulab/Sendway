namespace Sendway.Service.Endpoints.Admin;

// Sandbox: the tenant's messages are simulated and never leave the instance (see DeliverySimulator).
public sealed record CreateTenantRequest(string? Name, bool Sandbox = false);
