using Sendway.Core;

namespace Sendway.Service.Endpoints.Admin;

public static class GetAuditEventsEndpoint
{
    public const int DefaultLimit = 100;
    public const int MaxLimit = 1000;

    public static void MapGetAuditEventsEndpoint(this IEndpointRouteBuilder group)
    {
        // Newest first. For the next page, pass the id of the last event received as `before`.
        group.MapGet("/audit", async (Guid? tenantId, Guid? before, int? limit, IAuditLog auditLog, CancellationToken cancellationToken) =>
        {
            if (limit is < 1 or > MaxLimit)
            {
                return ApiProblem.BadRequest($"limit must be between 1 and {MaxLimit}.");
            }

            AuditCursor? cursor = null;
            if (before is { } beforeId)
            {
                if (await auditLog.GetAsync(beforeId, cancellationToken) is not { } last)
                {
                    return ApiProblem.BadRequest($"before must be the id of an audit event; '{beforeId}' is not one.");
                }

                cursor = new AuditCursor(last.OccurredAt, last.Id);
            }

            var events = await auditLog.ListAsync(tenantId, cursor, limit ?? DefaultLimit, cancellationToken);

            return Results.Ok(events.Select(e => new AuditEventResponse(e.Id, e.OccurredAt, e.Actor, e.Action, e.TenantId, e.Subject, e.RemoteAddress)));
        });
    }
}

public sealed record AuditEventResponse(
    Guid Id,
    DateTimeOffset OccurredAt,
    string Actor,
    string Action,
    Guid? TenantId,
    string? Subject,
    string? RemoteAddress);
