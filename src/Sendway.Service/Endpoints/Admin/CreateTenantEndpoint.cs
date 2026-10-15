using Sendway.Core;

namespace Sendway.Service.Endpoints.Admin;

public static class CreateTenantEndpoint
{
    public static void MapCreateTenantEndpoint(this IEndpointRouteBuilder group)
    {
        group.MapPost("/tenants", async (CreateTenantRequest request, HttpContext httpContext, ITenantStore tenantStore, CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return ApiProblem.BadRequest("name is required.");
            }

            var (plaintextKey, hash) = ApiKeyGenerator.Generate(request.Sandbox);
            var tenant = await tenantStore.CreateAsync(request.Name, hash, request.Sandbox, cancellationToken);
            await AdminAudit.RecordAsync(httpContext, tenant.Id, request.Sandbox ? tenant.Name + " (sandbox)" : tenant.Name);

            return Results.Ok(new CreateTenantResponse(tenant.Id, tenant.Name, plaintextKey, tenant.Sandbox));
        }).Audited("tenant.created");
    }
}
