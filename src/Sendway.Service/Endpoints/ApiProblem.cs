namespace Sendway.Service.Endpoints;

// Every error this API answers with a body uses one shape: RFC 9457 problem details, with a `code`
// a caller can branch on and a `detail` meant for a person reading a log. Codes are part of the
// contract and stay few and stable; the wording of `detail` is not.
public static class ApiProblem
{
    // The request itself is wrong (a missing field, a size limit, an unusable value). Sending it
    // again unchanged gets the same answer.
    public const string InvalidRequest = "invalid_request";

    // A destination address is malformed.
    public const string InvalidRecipient = "invalid_recipient";

    // The Idempotency-Key was already used for a different request.
    public const string IdempotencyKeyReused = "idempotency_key_reused";

    // The tenant's request rate limit was exceeded; see the Retry-After header.
    public const string RateLimited = "rate_limited";

    public static IResult BadRequest(string detail, string code = InvalidRequest) =>
        Create(StatusCodes.Status400BadRequest, code, detail);

    public static IResult Create(int statusCode, string code, string detail) =>
        Results.Problem(
            detail: detail,
            statusCode: statusCode,
            extensions: new Dictionary<string, object?> { ["code"] = code });
}
