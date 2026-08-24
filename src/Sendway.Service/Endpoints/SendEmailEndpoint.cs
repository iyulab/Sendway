using Sendway.Core;
using Sendway.Service.Auth;

namespace Sendway.Service.Endpoints;

public static class SendEmailEndpoint
{
    // Bounds request size before it reaches SmtpEmailSender — protects the process from an
    // oversized payload rather than validating email formatting (SmtpEmailSender/InvalidRecipientException
    // already own that). Subject matches the common 200-char convention for email subject lines;
    // To follows RFC 5321's 320-character maximum address length; Body is generous for a
    // transactional plain-text message while still being a bounded number, not "however much fits
    // in the request body."
    private const int MaxToLength = 320;
    private const int MaxSubjectLength = 200;
    private const int MaxBodyLength = 1_000_000;

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

            if (request.To.Length > MaxToLength)
            {
                return Results.BadRequest(new { error = $"to는 {MaxToLength}자를 초과할 수 없습니다." });
            }

            if (request.Subject.Length > MaxSubjectLength)
            {
                return Results.BadRequest(new { error = $"subject는 {MaxSubjectLength}자를 초과할 수 없습니다." });
            }

            if (request.Body.Length > MaxBodyLength)
            {
                return Results.BadRequest(new { error = $"body는 {MaxBodyLength}자를 초과할 수 없습니다." });
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
