using Sendway.Core;
using Sendway.Service.Auth;

namespace Sendway.Service.Endpoints;

public static class GetMessageStatusEndpoint
{
    public static void MapGetMessageStatusEndpoint(this IEndpointRouteBuilder group)
    {
        group.MapGet("/{id:guid}", async (Guid id, HttpContext httpContext, IMessageStatusStore store) =>
        {
            var tenant = httpContext.GetTenant();
            var record = await store.GetAsync(id);

            if (record is null || record.TenantId != tenant.Id)
            {
                return Results.NotFound();
            }

            return Results.Ok(new MessageStatusResponse(
                record.Id,
                record.Channel,
                record.Recipient,
                record.Status.ToString(),
                record.Error,
                record.SentAt));
        });
    }
}
