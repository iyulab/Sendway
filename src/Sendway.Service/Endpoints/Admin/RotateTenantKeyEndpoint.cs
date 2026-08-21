using Sendway.Core;

namespace Sendway.Service.Endpoints.Admin;

public static class RotateTenantKeyEndpoint
{
    public static void MapRotateTenantKeyEndpoint(this IEndpointRouteBuilder group)
    {
        group.MapPost("/tenants/{id:guid}/rotate-key", async (Guid id, ITenantStore tenantStore, CancellationToken cancellationToken) =>
        {
            var (plaintextKey, hash) = ApiKeyGenerator.Generate();
            var updated = await tenantStore.SetApiKeyHashAsync(id, hash, cancellationToken);

            return updated ? Results.Ok(new RotateTenantKeyResponse(plaintextKey)) : Results.NotFound();
        });
    }
}
