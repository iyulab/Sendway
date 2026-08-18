namespace Sendway.Service.Endpoints;

public sealed record SendEmailRequest(string? To, string? Subject, string? Body);
