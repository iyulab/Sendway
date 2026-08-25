using Sendway.Core;
using Sendway.Service.Auth;

namespace Sendway.Service.Endpoints;

public static class SendEmailEndpoint
{
    // Bounds request size before it reaches SmtpEmailSender — protects the process from an
    // oversized payload rather than validating email formatting (SmtpEmailSender/InvalidRecipientException
    // already own that). MaxAddressLength follows RFC 5321's 320-character maximum address length and
    // applies to every individual address across to/cc/bcc. Subject matches the common 200-char
    // convention for email subject lines; Body is generous for a transactional plain-text message
    // while still being a bounded number, not "however much fits in the request body."
    // MaxRecipientsPerMessage (to+cc+bcc combined) matches the limit SendGrid and Mailgun both
    // converge on for a single send call — an anti-spam-vector bound, not a throughput target.
    private const int MaxAddressLength = 320;
    private const int MaxSubjectLength = 200;
    private const int MaxBodyLength = 1_000_000;
    private const int MaxRecipientsPerMessage = 1000;

    public static void MapSendEmailEndpoint(this IEndpointRouteBuilder group)
    {
        group.MapPost("/email", async (SendEmailRequest request, HttpContext httpContext, IEmailSender sender, IMessageStatusStore statusStore, CancellationToken cancellationToken) =>
        {
            if (request.To is null || request.To.Count == 0 ||
                string.IsNullOrWhiteSpace(request.Subject) ||
                string.IsNullOrWhiteSpace(request.Body))
            {
                return Results.BadRequest(new { error = "to(최소 1개), subject, body는 모두 필수입니다." });
            }

            var cc = request.Cc ?? [];
            var bcc = request.Bcc ?? [];

            foreach (var address in request.To.Concat(cc).Concat(bcc))
            {
                if (string.IsNullOrWhiteSpace(address))
                {
                    return Results.BadRequest(new { error = "to/cc/bcc에는 빈 주소를 포함할 수 없습니다." });
                }

                if (address.Length > MaxAddressLength)
                {
                    return Results.BadRequest(new { error = $"주소는 {MaxAddressLength}자를 초과할 수 없습니다: '{address}'" });
                }
            }

            var totalRecipients = request.To.Count + cc.Count + bcc.Count;
            if (totalRecipients > MaxRecipientsPerMessage)
            {
                return Results.BadRequest(new { error = $"수신자(to+cc+bcc)는 총 {MaxRecipientsPerMessage}명을 초과할 수 없습니다." });
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
            var recipientSummary = BuildRecipientSummary(request.To, cc, bcc);

            try
            {
                var message = new EmailMessage(tenant.Id, request.To, request.Subject, request.Body, cc, bcc);
                await sender.SendAsync(message, cancellationToken);
                // statusStore writes intentionally use no cancellation token — the outcome should be
                // durably recorded even if the caller disconnected before the response could be sent.
                var id = await statusStore.RecordAsync(tenant.Id, "email", recipientSummary, MessageDeliveryStatus.Sent, error: null);
                return Results.Ok(new SendMessageResponse(id));
            }
            catch (InvalidRecipientException ex)
            {
                var id = await statusStore.RecordAsync(tenant.Id, "email", recipientSummary, MessageDeliveryStatus.Failed, ex.Message);
                return Results.BadRequest(new SendMessageFailureResponse(ex.Message, id));
            }
            catch (Exception ex) when (ex is not ArgumentException)
            {
                var id = await statusStore.RecordAsync(tenant.Id, "email", recipientSummary, MessageDeliveryStatus.Failed, ex.Message);
                return Results.Problem(
                    detail: ex.Message,
                    statusCode: StatusCodes.Status502BadGateway,
                    extensions: new Dictionary<string, object?> { ["messageId"] = id });
            }
        });
    }

    // MessageRecord.Recipient stays a single string (no response-shape break) — this folds the full
    // to/cc/bcc set into one auditable value instead of introducing a new list-shaped field.
    private static string BuildRecipientSummary(IReadOnlyList<string> to, IReadOnlyList<string> cc, IReadOnlyList<string> bcc)
    {
        var parts = new List<string> { string.Join(", ", to) };

        if (cc.Count > 0)
        {
            parts.Add($"cc: {string.Join(", ", cc)}");
        }

        if (bcc.Count > 0)
        {
            parts.Add($"bcc: {string.Join(", ", bcc)}");
        }

        return string.Join("; ", parts);
    }
}
