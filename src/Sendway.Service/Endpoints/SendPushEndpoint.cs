using Sendway.Core;
using Sendway.Service.Auth;

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
        group.MapPost("/push", async (SendPushRequest request, HttpContext httpContext, IMessageOutbox outbox, MessageDeliverySignal signal) =>
        {
            if (string.IsNullOrWhiteSpace(request.DeviceToken) ||
                string.IsNullOrWhiteSpace(request.Title) ||
                string.IsNullOrWhiteSpace(request.Body))
            {
                return ApiProblem.BadRequest("deviceToken, title and body are required.");
            }

            if (request.DeviceToken.Length > MaxDeviceTokenLength)
            {
                return ApiProblem.BadRequest($"deviceToken must not exceed {MaxDeviceTokenLength} characters.");
            }

            if (request.Title.Length > MaxTitleLength)
            {
                return ApiProblem.BadRequest($"title must not exceed {MaxTitleLength} characters.");
            }

            if (request.Body.Length > MaxBodyLength)
            {
                return ApiProblem.BadRequest($"body must not exceed {MaxBodyLength} characters.");
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
                MessagePayload.Serialize(message),
                request.ExpiresAt);
        })
        .WithName("SendPush")
        .WithSummary("Accept an app push notification for delivery")
        .Produces<SendMessageResponse>(StatusCodes.Status202Accepted)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
        .ProducesProblem(StatusCodes.Status429TooManyRequests);
    }
}
