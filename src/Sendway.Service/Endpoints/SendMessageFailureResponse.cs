namespace Sendway.Service.Endpoints;

public sealed record SendMessageFailureResponse(string Error, Guid MessageId);
