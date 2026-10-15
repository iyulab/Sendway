using Sendway.Core;

namespace Sendway.Service.Endpoints.Admin;

public static class RotateTenantKeyEndpoint
{
    // A week covers a calling service's slowest ordinary release; an overlap longer than that is a
    // second key nobody remembers to retire.
    public const int MaxPreviousKeyValidForSeconds = 7 * 24 * 60 * 60;

    public static void MapRotateTenantKeyEndpoint(this IEndpointRouteBuilder group)
    {
        // Without a body the old key stops at once — the safe default when a key has leaked. A planned
        // rotation names how long the old key may keep working while the calling service switches.
        group.MapPost("/tenants/{id:guid}/rotate-key", async (
            Guid id,
            RotateTenantKeyRequest? request,
            HttpContext httpContext,
            ITenantStore tenantStore,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var overlap = request?.PreviousKeyValidForSeconds ?? 0;
            if (overlap is < 0 or > MaxPreviousKeyValidForSeconds)
            {
                return ApiProblem.BadRequest($"previousKeyValidForSeconds must be between 0 and {MaxPreviousKeyValidForSeconds}.");
            }

            DateTimeOffset? previousKeyExpiresAt = overlap == 0 ? null : timeProvider.GetUtcNow().AddSeconds(overlap);

            if (await tenantStore.GetByIdAsync(id, cancellationToken) is not { } tenant)
            {
                return Results.NotFound();
            }

            var (plaintextKey, hash) = ApiKeyGenerator.Generate(tenant.Sandbox);
            if (!await tenantStore.RotateApiKeyAsync(id, hash, previousKeyExpiresAt, cancellationToken))
            {
                return Results.NotFound();
            }

            await AdminAudit.RecordAsync(httpContext, id, previousKeyExpiresAt is { } expiresAt ? $"previous key until {expiresAt:O}" : null);
            return Results.Ok(new RotateTenantKeyResponse(plaintextKey, previousKeyExpiresAt));
        }).Audited("tenant.key_rotated");

        group.MapDelete("/tenants/{id:guid}/previous-key", async (
            Guid id,
            HttpContext httpContext,
            ITenantStore tenantStore,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            if (!await tenantStore.RevokePreviousApiKeyAsync(id, timeProvider.GetUtcNow(), cancellationToken))
            {
                return Results.NotFound();
            }

            await AdminAudit.RecordAsync(httpContext, id);
            return Results.NoContent();
        }).Audited("tenant.previous_key_revoked");
    }
}
