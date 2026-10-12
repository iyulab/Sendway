using Sendway.Core;
using Sendway.Service.Auth;
using Sendway.Service.Delivery;

namespace Sendway.Service.Endpoints;

public static class SendPushEndpoint
{
    // Bounds request size before the message is stored. DeviceToken is generous since FCM
    // registration tokens have no fixed spec'd length; Title/Body are kept well under FCM's ~4KB
    // total message-payload limit so an oversized push is rejected here with a clear 400 instead of
    // failing downstream at FCM.
    private const int MaxDeviceTokenLength = 4096;
    private const int MaxTitleLength = 200;
    private const int MaxBodyLength = 4000;

    public static void MapSendPushEndpoint(this IEndpointRouteBuilder group)
    {
        group.MapPost("/push", async (SendPushRequest request, HttpContext httpContext, IMessageOutbox outbox, DeliverySignal signal) =>
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

            // Whether a device token is still registered is only known to the push provider, so an
            // unregistered token is accepted here and surfaces as a Failed message afterwards.
            var message = new PushMessage(httpContext.GetTenant().Id, request.DeviceToken, request.Title, request.Body);

            return await MessageAcceptance.AcceptAsync(
                httpContext,
                outbox,
                signal,
                MessageChannels.Push,
                request.DeviceToken,
                MessagePayload.Serialize(message));
        });
    }
}
