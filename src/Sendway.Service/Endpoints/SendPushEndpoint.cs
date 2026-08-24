using Sendway.Core;
using Sendway.Service.Auth;

namespace Sendway.Service.Endpoints;

public static class SendPushEndpoint
{
    // Bounds request size before it reaches FcmPushSender. DeviceToken is generous since FCM
    // registration tokens have no fixed spec'd length; Title/Body are kept well under FCM's ~4KB
    // total message-payload limit so an oversized push is rejected here with a clear 400 instead of
    // failing downstream at FCM.
    private const int MaxDeviceTokenLength = 4096;
    private const int MaxTitleLength = 200;
    private const int MaxBodyLength = 4000;

    public static void MapSendPushEndpoint(this IEndpointRouteBuilder group)
    {
        group.MapPost("/push", async (SendPushRequest request, HttpContext httpContext, IPushSender sender, IMessageStatusStore statusStore, CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.DeviceToken) ||
                string.IsNullOrWhiteSpace(request.Title) ||
                string.IsNullOrWhiteSpace(request.Body))
            {
                return Results.BadRequest(new { error = "deviceToken, title, body는 모두 필수입니다." });
            }

            if (request.DeviceToken.Length > MaxDeviceTokenLength)
            {
                return Results.BadRequest(new { error = $"deviceToken는 {MaxDeviceTokenLength}자를 초과할 수 없습니다." });
            }

            if (request.Title.Length > MaxTitleLength)
            {
                return Results.BadRequest(new { error = $"title은 {MaxTitleLength}자를 초과할 수 없습니다." });
            }

            if (request.Body.Length > MaxBodyLength)
            {
                return Results.BadRequest(new { error = $"body는 {MaxBodyLength}자를 초과할 수 없습니다." });
            }

            var tenant = httpContext.GetTenant();

            try
            {
                var message = new PushMessage(tenant.Id, request.DeviceToken, request.Title, request.Body);
                await sender.SendAsync(message, cancellationToken);
                // statusStore writes intentionally use no cancellation token — the outcome should be
                // durably recorded even if the caller disconnected before the response could be sent.
                var id = await statusStore.RecordAsync(tenant.Id, "push", request.DeviceToken, MessageDeliveryStatus.Sent, error: null);
                return Results.Ok(new SendMessageResponse(id));
            }
            catch (InvalidRecipientException ex)
            {
                var id = await statusStore.RecordAsync(tenant.Id, "push", request.DeviceToken, MessageDeliveryStatus.Failed, ex.Message);
                return Results.BadRequest(new SendMessageFailureResponse(ex.Message, id));
            }
            catch (Exception ex) when (ex is not ArgumentException)
            {
                var id = await statusStore.RecordAsync(tenant.Id, "push", request.DeviceToken, MessageDeliveryStatus.Failed, ex.Message);
                return Results.Problem(
                    detail: ex.Message,
                    statusCode: StatusCodes.Status502BadGateway,
                    extensions: new Dictionary<string, object?> { ["messageId"] = id });
            }
        });
    }
}
