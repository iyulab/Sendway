using Sendway.Core;

namespace Sendway.Service.Endpoints.Admin;

public static class ListTenantsEndpoint
{
    public static void MapListTenantsEndpoint(this IEndpointRouteBuilder group)
    {
        group.MapGet("/tenants", async (ITenantStore tenantStore, CancellationToken cancellationToken) =>
        {
            var tenants = await tenantStore.ListAsync(cancellationToken);

            return Results.Ok(tenants.Select(t => new TenantResponse(t.Id, t.Name, t.Active, t.CreatedAt)));
        });
    }
}
