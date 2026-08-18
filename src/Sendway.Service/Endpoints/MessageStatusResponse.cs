namespace Sendway.Service.Endpoints;

public sealed record MessageStatusResponse(
    Guid Id,
    string Channel,
    string Recipient,
    string Status,
    string? Error,
    DateTimeOffset SentAt);
