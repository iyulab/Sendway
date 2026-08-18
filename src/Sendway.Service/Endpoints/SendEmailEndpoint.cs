using Sendway.Core;

namespace Sendway.Service.Endpoints;

public static class SendEmailEndpoint
{
    public static void MapSendEmailEndpoint(this WebApplication app)
    {
        app.MapPost("/messages/email", async (SendEmailRequest request, IEmailSender sender) =>
        {
            if (string.IsNullOrWhiteSpace(request.To) ||
                string.IsNullOrWhiteSpace(request.Subject) ||
                string.IsNullOrWhiteSpace(request.Body))
            {
                return Results.BadRequest(new { error = "to, subject, body는 모두 필수입니다." });
            }

            try
            {
                var message = new EmailMessage(request.To, request.Subject, request.Body);
                await sender.SendAsync(message);
                return Results.Ok();
            }
            catch (Exception ex) when (ex is not ArgumentException)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status502BadGateway);
            }
        });
    }
}
