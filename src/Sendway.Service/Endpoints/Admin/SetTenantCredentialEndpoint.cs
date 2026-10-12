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
                    var (options, error) = await ReadAsync<SmtpOptions>(request, ChannelCredentialValidator.Validate, cancellationToken);
                    if (options is null)
                    {
                        return ApiProblem.BadRequest(error!);
                    }

                    await credentialStore.SetAsync(tenant.Id, ChannelCredentialNames.Smtp, options, cancellationToken);
                    return Results.NoContent();
                }
                case ChannelCredentialNames.EmailGraph:
                {
                    var (options, error) = await ReadAsync<GraphOptions>(request, ChannelCredentialValidator.Validate, cancellationToken);
                    if (options is null)
                    {
                        return ApiProblem.BadRequest(error!);
                    }

                    await credentialStore.SetAsync(tenant.Id, ChannelCredentialNames.EmailGraph, options, cancellationToken);
                    // GraphEmailSender caches one GraphServiceClient per tenant for the process
                    // lifetime — same reason FcmPushSender needs InvalidateTenant below.
                    graphEmailSender.InvalidateTenant(tenant.Id);
                    return Results.NoContent();
                }
                case ChannelCredentialNames.Fcm:
                {
                    var (options, error) = await ReadAsync<FcmOptions>(request, ChannelCredentialValidator.Validate, cancellationToken);
                    if (options is null)
                    {
                        return ApiProblem.BadRequest(error!);
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
                    return ApiProblem.BadRequest($"Unknown channel '{channel}'.");
            }
        });
    }

    // Reads and checks the credential. A body that isn't JSON, or lacks a required property, is the
    // caller's mistake and is answered as one — left to the framework it would surface as a 500.
    private static async Task<(T? Options, string? Error)> ReadAsync<T>(
        HttpRequest request,
        Func<T, string?> validate,
        CancellationToken cancellationToken) where T : class
    {
        T? options;
        try
        {
            options = await request.ReadFromJsonAsync<T>(cancellationToken);
        }
        catch (JsonException ex)
        {
            return (null, $"The credential could not be read: {ex.Message}");
        }

        if (options is null)
        {
            return (null, "A credential is required.");
        }

        return validate(options) is { } error ? (null, error) : (options, null);
    }
}
