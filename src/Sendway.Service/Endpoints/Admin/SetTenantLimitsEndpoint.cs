using Sendway.Core;

namespace Sendway.Service.Endpoints.Admin;

// Replaces a tenant's own limits. A field left out (or null) falls back to the deployment's default.
public sealed record SetTenantLimitsRequest(IReadOnlyList<string>? SmsCountries, int? SmsPerDay, int? EmailPerDay, int? PushPerDay);

public static class SetTenantLimitsEndpoint
{
    public static void MapSetTenantLimitsEndpoint(this IEndpointRouteBuilder group)
    {
        group.MapPut("/tenants/{id:guid}/limits", async (Guid id, SetTenantLimitsRequest request, HttpContext httpContext, ITenantStore tenantStore, CancellationToken cancellationToken) =>
        {
            if (request.SmsCountries?.Any(code => code.Length is < 1 or > 3 || !code.All(char.IsAsciiDigit)) == true)
            {
                return ApiProblem.BadRequest("smsCountries are country calling codes without '+', e.g. [\"82\", \"81\"].");
            }

            if (new[] { request.SmsPerDay, request.EmailPerDay, request.PushPerDay }.Any(limit => limit < 0))
            {
                return ApiProblem.BadRequest("A per-day limit must not be negative.");
            }

            var countries = request.SmsCountries is null ? null : string.Join(',', request.SmsCountries.Distinct());
            if (!await tenantStore.SetLimitsAsync(id, countries, request.SmsPerDay, request.EmailPerDay, request.PushPerDay, cancellationToken))
            {
                return Results.NotFound();
            }

            await AdminAudit.RecordAsync(httpContext, id,
                $"smsCountries={countries ?? "default"}; smsPerDay={request.SmsPerDay?.ToString() ?? "default"}; " +
                $"emailPerDay={request.EmailPerDay?.ToString() ?? "default"}; pushPerDay={request.PushPerDay?.ToString() ?? "default"}");
            return Results.NoContent();
        }).Audited("tenant.limits_set");
    }
}
