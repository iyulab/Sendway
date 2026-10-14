using System.Security.Claims;
using Sendway.Core;

namespace Sendway.Service;

// The action an administrative endpoint records when it changes something. Carried as endpoint
// metadata so the name lives next to the route, and so a test can check that every endpoint that
// changes state declares one.
public sealed record AuditAction(string Name);

public static class AdminAudit
{
    public static RouteHandlerBuilder Audited(this RouteHandlerBuilder builder, string action) =>
        builder.WithMetadata(new AuditAction(action));

    // Records the current endpoint's action, after the change it describes has been stored. A
    // failure to record is logged, not returned: the change has already happened, and answering 500
    // would invite the caller to repeat it.
    public static async Task RecordAsync(HttpContext httpContext, Guid? tenantId, string? subject = null)
    {
        var action = httpContext.GetEndpoint()?.Metadata.GetMetadata<AuditAction>()
            ?? throw new InvalidOperationException($"Endpoint '{httpContext.GetEndpoint()?.DisplayName}' records an audit event but declares no AuditAction.");

        var services = httpContext.RequestServices;
        var auditEvent = new AuditEvent
        {
            Id = Guid.NewGuid(),
            OccurredAt = services.GetRequiredService<TimeProvider>().GetUtcNow(),
            Actor = httpContext.User.FindFirstValue(ClaimTypes.Name) ?? "unknown",
            Action = action.Name,
            TenantId = tenantId,
            Subject = subject,
            RemoteAddress = httpContext.Connection.RemoteIpAddress?.ToString()
        };

        try
        {
            // Not the request's token: a caller that hangs up after the change must not take its
            // record with it.
            await services.GetRequiredService<IAuditLog>().AppendAsync(auditEvent, CancellationToken.None);
        }
        catch (Exception ex)
        {
            services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(AdminAudit))
                .LogError(ex, "Could not record audit event {Action} for tenant {TenantId} by {Actor}", auditEvent.Action, tenantId, auditEvent.Actor);
        }
    }
}
