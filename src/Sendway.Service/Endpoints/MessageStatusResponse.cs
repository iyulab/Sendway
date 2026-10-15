using Sendway.Core;

namespace Sendway.Service.Endpoints;

public sealed record MessageStatusResponse(
    Guid Id,
    string Channel,
    string Recipient,
    string Status,
    string? Error,
    string? Reason,
    IReadOnlyList<RefusedRecipient>? RefusedRecipients,
    int Attempts,
    DateTimeOffset AcceptedAt,
    DateTimeOffset? CompletedAt);
