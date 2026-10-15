namespace Sendway.Service.Endpoints;

public sealed record SendSmsRequest(string? To, string? Body, DateTimeOffset? ExpiresAt = null);
