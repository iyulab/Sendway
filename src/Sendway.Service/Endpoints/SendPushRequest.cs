namespace Sendway.Service.Endpoints;

public sealed record SendPushRequest(string? DeviceToken, string? Title, string? Body);
