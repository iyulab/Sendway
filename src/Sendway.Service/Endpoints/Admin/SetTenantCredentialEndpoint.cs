using Sendway.Core;

namespace Sendway.Service.Endpoints.Admin;

public static class SetTenantCredentialEndpoint
{
    public static void MapSetTenantCredentialEndpoint(this IEndpointRouteBuilder group)
    {
        group.MapPut("/tenants/{id:guid}/credentials/{channel}", async (
            Guid id,
            string channel,
            HttpRequest request,
            ITenantStore tenantStore,
            CancellationToken cancellationToken) =>
        {
            var tenant = await tenantStore.GetByIdAsync(id, cancellationToken);
            if (tenant is null)
            {
                return Results.NotFound();
            }

            if (ChannelCredentialKinds.Find(channel) is not { } kind)
            {
                return ApiProblem.BadRequest($"Unknown channel '{channel}'.");
            }

            return await kind.SetAsync(request, tenant.Id, cancellationToken) is { } error
                ? ApiProblem.BadRequest(error)
                : Results.NoContent();
        });
    }
}
