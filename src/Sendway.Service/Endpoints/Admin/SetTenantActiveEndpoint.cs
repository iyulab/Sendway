using Sendway.Core;

namespace Sendway.Service.Endpoints.Admin;

public static class SetTenantActiveEndpoint
{
    public static void MapSetTenantActiveEndpoint(this IEndpointRouteBuilder group)
    {
        group.MapPost("/tenants/{id:guid}/deactivate", (Guid id, HttpContext httpContext, ITenantStore tenantStore, CancellationToken cancellationToken) =>
            SetActiveAsync(id, active: false, httpContext, tenantStore, cancellationToken))
            .Audited("tenant.deactivated");

        group.MapPost("/tenants/{id:guid}/reactivate", (Guid id, HttpContext httpContext, ITenantStore tenantStore, CancellationToken cancellationToken) =>
            SetActiveAsync(id, active: true, httpContext, tenantStore, cancellationToken))
            .Audited("tenant.reactivated");
    }

    private static async Task<IResult> SetActiveAsync(Guid id, bool active, HttpContext httpContext, ITenantStore tenantStore, CancellationToken cancellationToken)
    {
        if (!await tenantStore.SetActiveAsync(id, active, cancellationToken))
        {
            return Results.NotFound();
        }

        await AdminAudit.RecordAsync(httpContext, id);
        return Results.NoContent();
    }
}
