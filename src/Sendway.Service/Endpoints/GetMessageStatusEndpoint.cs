using Sendway.Core;

namespace Sendway.Service.Endpoints;

public static class GetMessageStatusEndpoint
{
    public static void MapGetMessageStatusEndpoint(this WebApplication app)
    {
        app.MapGet("/messages/{id:guid}", async (Guid id, IMessageStatusStore store) =>
        {
            var record = await store.GetAsync(id);

            return record is null
                ? Results.NotFound()
                : Results.Ok(new MessageStatusResponse(
                    record.Id,
                    record.Channel,
                    record.Recipient,
                    record.Status.ToString(),
                    record.Error,
                    record.SentAt));
        });
    }
}
