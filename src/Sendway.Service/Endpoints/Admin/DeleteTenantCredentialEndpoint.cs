using Sendway.Core;

namespace Sendway.Service.Endpoints.Admin;

public static class DeleteTenantCredentialEndpoint
{
    public static void MapDeleteTenantCredentialEndpoint(this IEndpointRouteBuilder group)
    {
        group.MapDelete("/tenants/{id:guid}/credentials/{channel}", async (
            Guid id,
            string channel,
            HttpContext httpContext,
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

            await kind.DeleteAsync(httpContext, tenant.Id, cancellationToken);
            await AdminAudit.RecordAsync(httpContext, tenant.Id, kind.Name);
            return Results.NoContent();
        }).Audited("credential.deleted");
    }
}
