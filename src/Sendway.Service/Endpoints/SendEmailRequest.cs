namespace Sendway.Service.Endpoints;

public sealed record SendEmailRequest(List<string>? To, string? Subject, string? Body, List<string>? Cc = null, List<string>? Bcc = null, string? HtmlBody = null);
