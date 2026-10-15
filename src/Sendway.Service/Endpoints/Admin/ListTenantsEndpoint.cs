using Sendway.Core;

namespace Sendway.Service.Endpoints.Admin;

public static class ListTenantsEndpoint
{
    public static void MapListTenantsEndpoint(this IEndpointRouteBuilder group)
    {
        group.MapGet("/tenants", async (ITenantStore tenantStore, TimeProvider timeProvider, CancellationToken cancellationToken) =>
        {
            var tenants = await tenantStore.ListAsync(cancellationToken);
            var now = timeProvider.GetUtcNow();

            return Results.Ok(tenants.Select(t => new TenantResponse(t.Id, t.Name, t.Active, t.Sandbox, t.CreatedAt, t.PreviousApiKeyExpiryAt(now))));
        });
    }
}
