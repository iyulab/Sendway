using System.Text.Json;
using Sendway.Core;

namespace Sendway.Service.Endpoints.Admin;

public static class SetTenantCredentialEndpoint
{
    public static void MapSetTenantCredentialEndpoint(this IEndpointRouteBuilder group)
    {
        group.MapPut("/tenants/{id:guid}/credentials/{channel}", async (
            Guid id,
            string channel,
            JsonElement body,
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

            if (await kind.SetAsync(body, httpContext.RequestServices, tenant.Id, cancellationToken) is { } error)
            {
                return ApiProblem.BadRequest(error);
            }

            await AdminAudit.RecordAsync(httpContext, tenant.Id, kind.Name);
            return Results.NoContent();
        }).Audited("credential.set");
    }
}
