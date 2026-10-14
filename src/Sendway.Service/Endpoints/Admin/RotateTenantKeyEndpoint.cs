using Sendway.Core;

namespace Sendway.Service.Endpoints.Admin;

public static class RotateTenantKeyEndpoint
{
    public static void MapRotateTenantKeyEndpoint(this IEndpointRouteBuilder group)
    {
        group.MapPost("/tenants/{id:guid}/rotate-key", async (Guid id, HttpContext httpContext, ITenantStore tenantStore, CancellationToken cancellationToken) =>
        {
            var (plaintextKey, hash) = ApiKeyGenerator.Generate();
            if (!await tenantStore.SetApiKeyHashAsync(id, hash, cancellationToken))
            {
                return Results.NotFound();
            }

            await AdminAudit.RecordAsync(httpContext, id);
            return Results.Ok(new RotateTenantKeyResponse(plaintextKey));
        }).Audited("tenant.key_rotated");
    }
}
