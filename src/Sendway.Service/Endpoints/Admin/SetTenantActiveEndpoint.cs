using Sendway.Core;

namespace Sendway.Service.Endpoints.Admin;

public static class SetTenantActiveEndpoint
{
    public static void MapSetTenantActiveEndpoint(this IEndpointRouteBuilder group)
    {
        group.MapPost("/tenants/{id:guid}/deactivate", (Guid id, ITenantStore tenantStore, CancellationToken cancellationToken) =>
            SetActiveAsync(id, active: false, tenantStore, cancellationToken));

        group.MapPost("/tenants/{id:guid}/reactivate", (Guid id, ITenantStore tenantStore, CancellationToken cancellationToken) =>
            SetActiveAsync(id, active: true, tenantStore, cancellationToken));
    }

    private static async Task<IResult> SetActiveAsync(Guid id, bool active, ITenantStore tenantStore, CancellationToken cancellationToken)
    {
        var updated = await tenantStore.SetActiveAsync(id, active, cancellationToken);

        return updated ? Results.NoContent() : Results.NotFound();
    }
}
