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
                {
                    var options = await request.ReadFromJsonAsync<SmtpOptions>(cancellationToken);
                    if (options is null)
                    {
                        return Results.BadRequest(new { error = "invalid smtp credential payload" });
                    }

                    await credentialStore.SetAsync(tenant.Id, ChannelCredentialNames.Smtp, options, cancellationToken);
                    return Results.NoContent();
                }
                case ChannelCredentialNames.EmailGraph:
                {
                    var options = await request.ReadFromJsonAsync<GraphOptions>(cancellationToken);
                    if (options is null)
                    {
                        return Results.BadRequest(new { error = "invalid email-graph credential payload" });
                    }

                    await credentialStore.SetAsync(tenant.Id, ChannelCredentialNames.EmailGraph, options, cancellationToken);
                    // GraphEmailSender caches one GraphServiceClient per tenant for the process
                    // lifetime — same reason FcmPushSender needs InvalidateTenant below.
                    graphEmailSender.InvalidateTenant(tenant.Id);
                    return Results.NoContent();
                }
                case ChannelCredentialNames.Fcm:
                {
                    var options = await request.ReadFromJsonAsync<FcmOptions>(cancellationToken);
                    if (options is null)
                    {
                        return Results.BadRequest(new { error = "invalid fcm credential payload" });
                    }

                    await credentialStore.SetAsync(tenant.Id, ChannelCredentialNames.Fcm, options, cancellationToken);
                    // FcmPushSender caches one FirebaseApp per tenant for the process lifetime —
                    // without this, a rotated/newly-set credential would be silently ignored until
                    // restart. IPushSender itself has no invalidation method (kept channel-agnostic,
                    // no reason for SmtpEmailSender to grow it too) so this is a concrete-type check.
                    if (pushSender is FcmPushSender fcmPushSender)
                    {
                        fcmPushSender.InvalidateTenant(tenant.Id);
                    }
                    return Results.NoContent();
                }
                default:
                    return Results.BadRequest(new { error = $"unknown channel '{channel}'" });
            }
        });
    }
}
