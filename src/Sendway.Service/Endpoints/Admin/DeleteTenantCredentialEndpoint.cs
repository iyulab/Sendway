using Sendway.Core;

namespace Sendway.Service.Endpoints.Admin;

public static class DeleteTenantCredentialEndpoint
{
    public static void MapDeleteTenantCredentialEndpoint(this IEndpointRouteBuilder group)
    {
        group.MapDelete("/tenants/{id:guid}/credentials/{channel}", async (
            Guid id,
            string channel,
            ITenantStore tenantStore,
            ICredentialStore credentialStore,
            IPushSender pushSender,
            GraphEmailSender graphEmailSender,
            CancellationToken cancellationToken) =>
        {
            var tenant = await tenantStore.GetByIdAsync(id, cancellationToken);
            if (tenant is null)
            {
                return Results.NotFound();
            }

            switch (channel)
            {
                case ChannelCredentialNames.Smtp:
                    await credentialStore.DeleteAsync(tenant.Id, ChannelCredentialNames.Smtp, cancellationToken);
                    return Results.NoContent();
                case ChannelCredentialNames.EmailGraph:
                    await credentialStore.DeleteAsync(tenant.Id, ChannelCredentialNames.EmailGraph, cancellationToken);
                    // Same reason SetTenantCredentialEndpoint invalidates on PUT — a cached
                    // GraphServiceClient must not keep serving the just-deleted credential.
                    graphEmailSender.InvalidateTenant(tenant.Id);
                    return Results.NoContent();
                case ChannelCredentialNames.Fcm:
                    await credentialStore.DeleteAsync(tenant.Id, ChannelCredentialNames.Fcm, cancellationToken);
                    if (pushSender is FcmPushSender fcmPushSender)
                    {
                        fcmPushSender.InvalidateTenant(tenant.Id);
                    }
                    return Results.NoContent();
                default:
                    return Results.BadRequest(new { error = $"unknown channel '{channel}'" });
            }
        });
    }
}
