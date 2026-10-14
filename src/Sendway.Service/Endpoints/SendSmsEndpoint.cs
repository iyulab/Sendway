using Sendway.Core;
using Sendway.Service.Auth;

namespace Sendway.Service.Endpoints;

public static class SendSmsEndpoint
{
    public static void MapSendSmsEndpoint(this IEndpointRouteBuilder group)
    {
        group.MapPost("/sms", async (SendSmsRequest request, HttpContext httpContext, ISmsSender smsSender, IMessageOutbox outbox, MessageDeliverySignal signal) =>
        {
            if (string.IsNullOrWhiteSpace(request.To) || string.IsNullOrWhiteSpace(request.Body))
            {
                return ApiProblem.BadRequest("to and body are required.");
            }

            SmsMessage message;
            try
            {
                message = new SmsMessage(httpContext.GetTenant().Id, request.To, request.Body);
            }
            catch (InvalidRecipientException ex)
            {
                return ApiProblem.BadRequest(ex.Message, ApiProblem.InvalidRecipient);
            }

            // A destination the provider does not serve, or a body it cannot carry, is known now —
            // refused here rather than accepted and reported as a failed delivery later.
            if (smsSender.Check(message) is { } problem)
            {
                return ApiProblem.BadRequest(problem);
            }

            return await MessageAcceptance.AcceptAsync(
                httpContext,
                outbox,
                signal,
                MessageChannels.Sms,
                message.To.ToString(),
                MessagePayload.Serialize(message));
        });
    }
}
