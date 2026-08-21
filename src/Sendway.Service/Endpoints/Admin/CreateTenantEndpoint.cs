using Sendway.Core;

namespace Sendway.Service.Endpoints.Admin;

public static class CreateTenantEndpoint
{
    public static void MapCreateTenantEndpoint(this IEndpointRouteBuilder group)
    {
        group.MapPost("/tenants", async (CreateTenantRequest request, ITenantStore tenantStore, CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return Results.BadRequest(new { error = "name은 필수입니다." });
            }

            var (plaintextKey, hash) = ApiKeyGenerator.Generate();
            var tenant = await tenantStore.CreateAsync(request.Name, hash, cancellationToken);

            return Results.Ok(new CreateTenantResponse(tenant.Id, tenant.Name, plaintextKey));
        });
    }
}
