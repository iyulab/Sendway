using Sendway.Core;

namespace Sendway.Service.Endpoints;

public static class SendPushEndpoint
{
    public static void MapSendPushEndpoint(this WebApplication app)
    {
        app.MapPost("/messages/push", async (SendPushRequest request, IPushSender sender) =>
        {
            if (string.IsNullOrWhiteSpace(request.DeviceToken) ||
                string.IsNullOrWhiteSpace(request.Title) ||
                string.IsNullOrWhiteSpace(request.Body))
            {
                return Results.BadRequest(new { error = "deviceToken, title, body는 모두 필수입니다." });
            }

            try
            {
                var message = new PushMessage(request.DeviceToken, request.Title, request.Body);
                await sender.SendAsync(message);
                return Results.Ok();
            }
            catch (InvalidRecipientException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (Exception ex) when (ex is not ArgumentException)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status502BadGateway);
            }
        });
    }
}
