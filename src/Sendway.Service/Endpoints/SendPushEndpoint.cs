using Sendway.Core;

namespace Sendway.Service.Endpoints;

public static class SendPushEndpoint
{
    public static void MapSendPushEndpoint(this WebApplication app)
    {
        app.MapPost("/messages/push", async (SendPushRequest request, IPushSender sender, IMessageStatusStore statusStore, CancellationToken cancellationToken) =>
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
                await sender.SendAsync(message, cancellationToken);
                // statusStore writes intentionally use no cancellation token — the outcome should be
                // durably recorded even if the caller disconnected before the response could be sent.
                var id = await statusStore.RecordAsync("push", request.DeviceToken, MessageDeliveryStatus.Sent, error: null);
                return Results.Ok(new SendMessageResponse(id));
            }
            catch (InvalidRecipientException ex)
            {
                var id = await statusStore.RecordAsync("push", request.DeviceToken, MessageDeliveryStatus.Failed, ex.Message);
                return Results.BadRequest(new SendMessageFailureResponse(ex.Message, id));
            }
            catch (Exception ex) when (ex is not ArgumentException)
            {
                var id = await statusStore.RecordAsync("push", request.DeviceToken, MessageDeliveryStatus.Failed, ex.Message);
                return Results.Problem(
                    detail: ex.Message,
                    statusCode: StatusCodes.Status502BadGateway,
                    extensions: new Dictionary<string, object?> { ["messageId"] = id });
            }
        });
    }
}
