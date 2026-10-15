namespace Sendway.Service.Endpoints;

public sealed record MessageStatusResponse(
    Guid Id,
    string Channel,
    string Recipient,
    string Status,
    string? Error,
    string? Reason,
    int Attempts,
    DateTimeOffset AcceptedAt,
    DateTimeOffset? CompletedAt);
