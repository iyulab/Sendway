using Sendway.Core;

namespace Sendway.Service.Endpoints.Admin;

// The shared credentials every tenant without an override uses. A channel whose shared value
// configuration supplies is owned by configuration and refused here; otherwise it is set here and
// takes effect on the next send, without a restart.
public static class SharedCredentialEndpoints
{
    public const string FromConfiguration = "configuration";
    public const string FromApi = "api";

    public static void MapSharedCredentialEndpoints(this IEndpointRouteBuilder group)
    {
        group.MapGet("/credentials", async (IConfiguration configuration, ICredentialStore store, CancellationToken cancellationToken) =>
        {
            var credentials = new List<SharedCredentialResponse>();
            foreach (var kind in ChannelCredentialKinds.All.Where(kind => kind.Shareable))
            {
                var source = kind.IsConfigured(configuration) ? FromConfiguration
                    : await kind.HasSharedValueAsync(store, cancellationToken) ? FromApi
                    : null;
                credentials.Add(new SharedCredentialResponse(kind.Name, source));
            }

            return Results.Ok(credentials);
        });

        group.MapPut("/credentials/{channel}", async (string channel, HttpRequest request, IConfiguration configuration, CancellationToken cancellationToken) =>
        {
            if (Refusal(channel, configuration, out var kind) is { } refusal)
            {
                return refusal;
            }

            return await kind.SetAsync(request, tenantId: null, cancellationToken) is { } error
                ? ApiProblem.BadRequest(error)
                : Results.NoContent();
        });

        group.MapDelete("/credentials/{channel}", async (string channel, HttpContext httpContext, IConfiguration configuration, CancellationToken cancellationToken) =>
        {
            if (Refusal(channel, configuration, out var kind) is { } refusal)
            {
                return refusal;
            }

            await kind.DeleteAsync(httpContext, tenantId: null, cancellationToken);
            return Results.NoContent();
        });
    }

    // Why the shared value of this channel cannot be changed here, or null when it can (then `kind`
    // is the channel's entry).
    private static IResult? Refusal(string channel, IConfiguration configuration, out ChannelCredentialKind kind)
    {
        if (ChannelCredentialKinds.Find(channel) is not { } found)
        {
            kind = null!;
            return ApiProblem.BadRequest($"Unknown channel '{channel}'.");
        }

        kind = found;

        if (!kind.Shareable)
        {
            return ApiProblem.BadRequest($"'{channel}' has no shared value; set it per tenant.");
        }

        if (kind.IsConfigured(configuration))
        {
            return ApiProblem.Create(
                StatusCodes.Status409Conflict,
                ApiProblem.ManagedByConfiguration,
                $"The shared '{channel}' credential comes from configuration (the '{kind.ConfigurationSection}' section). " +
                "Change it there and restart, or remove the section to manage it here.");
        }

        return null;
    }
}

public sealed record SharedCredentialResponse(string Channel, string? Source);
