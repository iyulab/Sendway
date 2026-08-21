using Sendway.Core;
using Sendway.Service.Auth;

namespace Sendway.Service.Endpoints;

public static class SendEmailEndpoint
{
    public static void MapSendEmailEndpoint(this IEndpointRouteBuilder group)
    {
        group.MapPost("/email", async (SendEmailRequest request, HttpContext httpContext, IEmailSender sender, IMessageStatusStore statusStore, CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.To) ||
                string.IsNullOrWhiteSpace(request.Subject) ||
                string.IsNullOrWhiteSpace(request.Body))
            {
                return Results.BadRequest(new { error = "to, subject, body는 모두 필수입니다." });
            }

            var tenant = httpContext.GetTenant();

            try
            {
                var message = new EmailMessage(tenant.Id, request.To, request.Subject, request.Body);
                await sender.SendAsync(message, cancellationToken);
                // statusStore writes intentionally use no cancellation token — the outcome should be
                // durably recorded even if the caller disconnected before the response could be sent.
                var id = await statusStore.RecordAsync(tenant.Id, "email", request.To, MessageDeliveryStatus.Sent, error: null);
                return Results.Ok(new SendMessageResponse(id));
            }
            catch (InvalidRecipientException ex)
            {
                var id = await statusStore.RecordAsync(tenant.Id, "email", request.To, MessageDeliveryStatus.Failed, ex.Message);
                return Results.BadRequest(new SendMessageFailureResponse(ex.Message, id));
            }
            catch (Exception ex) when (ex is not ArgumentException)
            {
                var id = await statusStore.RecordAsync(tenant.Id, "email", request.To, MessageDeliveryStatus.Failed, ex.Message);
                return Results.Problem(
                    detail: ex.Message,
                    statusCode: StatusCodes.Status502BadGateway,
                    extensions: new Dictionary<string, object?> { ["messageId"] = id });
            }
        });
    }
}
