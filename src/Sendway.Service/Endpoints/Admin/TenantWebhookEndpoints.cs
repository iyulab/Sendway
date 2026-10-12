using Sendway.Core;

namespace Sendway.Service.Endpoints.Admin;

public static class TenantWebhookEndpoints
{
    public static void MapTenantWebhookEndpoints(this IEndpointRouteBuilder group)
    {
        group.MapPut("/tenants/{id:guid}/webhook", async (
            Guid id,
            SetTenantWebhookRequest request,
            ITenantStore tenantStore,
            IWebhookEndpointStore endpoints,
            CancellationToken cancellationToken) =>
        {
            if (await tenantStore.GetByIdAsync(id, cancellationToken) is null)
            {
                return Results.NotFound();
            }

            // Plain http is allowed alongside https: a self-hosted instance may deliver to a
            // receiver on the same private network, where TLS isn't always terminated.
            if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var url) ||
                (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp))
            {
                return ApiProblem.BadRequest("url must be an absolute http or https URL.");
            }

            var registration = await endpoints.SetAsync(id, url, cancellationToken);

            return Results.Ok(new TenantWebhookRegistrationResponse(registration.Url.AbsoluteUri, registration.Secret, registration.CreatedAt));
        });

        group.MapGet("/tenants/{id:guid}/webhook", async (Guid id, IWebhookEndpointStore endpoints, CancellationToken cancellationToken) =>
        {
            var endpoint = await endpoints.GetAsync(id, cancellationToken);

            return endpoint is null
                ? Results.NotFound()
                : Results.Ok(new TenantWebhookResponse(endpoint.Url.AbsoluteUri, endpoint.CreatedAt));
        });

        group.MapDelete("/tenants/{id:guid}/webhook", async (Guid id, IWebhookEndpointStore endpoints, CancellationToken cancellationToken) =>
            await endpoints.DeleteAsync(id, cancellationToken) ? Results.NoContent() : Results.NotFound());
    }
}

public sealed record SetTenantWebhookRequest(string? Url);

// The signing secret appears in this response only — it is not shown again, only replaced by
// registering the endpoint anew.
public sealed record TenantWebhookRegistrationResponse(string Url, string Secret, DateTimeOffset CreatedAt);

public sealed record TenantWebhookResponse(string Url, DateTimeOffset CreatedAt);
