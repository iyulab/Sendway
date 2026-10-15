using Microsoft.AspNetCore.Mvc;

namespace Sendway.Service.Endpoints;

// Every error this API answers uses one shape: RFC 9457 problem details, with a `code` a caller
// can branch on and a `detail` meant for a person reading a log. Codes are part of the contract
// and stay few and stable; the wording of `detail` is not.
public static class ApiProblem
{
    // The request itself is wrong (a missing field, a size limit, an unusable value). Sending it
    // again unchanged gets the same answer.
    public const string InvalidRequest = "invalid_request";

    // A destination address is malformed.
    public const string InvalidRecipient = "invalid_recipient";

    // The Idempotency-Key was already used for a different request.
    public const string IdempotencyKeyReused = "idempotency_key_reused";

    // A shared setting is supplied by configuration, so it cannot be changed through the API.
    public const string ManagedByConfiguration = "managed_by_configuration";

    // No credential lets this tenant send on the channel yet, its own or a shared one. Nothing was
    // stored; an administrator has to register one before the same request can be accepted.
    public const string ChannelNotConfigured = "channel_not_configured";

    // The tenant's request rate limit was exceeded; see the Retry-After header.
    public const string RateLimited = "rate_limited";

    // The tenant may not send SMS to that country (its allowed countries).
    public const string DestinationNotAllowed = "destination_not_allowed";

    // The tenant already sent as many messages on the channel in the last 24 hours as it may; see the
    // Retry-After header.
    public const string DailyLimitReached = "daily_limit_reached";

    // The API key is missing, unknown or belongs to a deactivated tenant.
    public const string Unauthorized = "unauthorized";

    // Nothing exists at this path, or the caller may not see it.
    public const string NotFound = "not_found";

    // The service failed; the request may be fine. Sending it again with the same Idempotency-Key
    // is safe.
    public const string InternalError = "internal_error";

    public static IResult BadRequest(string detail, string code = InvalidRequest) =>
        Create(StatusCodes.Status400BadRequest, code, detail);

    public static IResult Create(int statusCode, string code, string detail) =>
        Results.Problem(
            detail: detail,
            statusCode: statusCode,
            extensions: new Dictionary<string, object?> { ["code"] = code });

    // Fills in what an error is missing. Errors that no endpoint wrote (a body that cannot be read,
    // a path that does not exist, a failure nobody handled) arrive here with a status and nothing
    // else; errors an endpoint wrote already carry their own `code` and `detail` and keep them.
    public static void Complete(ProblemDetails problem)
    {
        var status = problem.Status ?? StatusCodes.Status500InternalServerError;
        problem.Extensions.TryAdd("code", CodeFor(status));
        problem.Detail ??= DetailFor(status);
    }

    private static string CodeFor(int status) => status switch
    {
        StatusCodes.Status401Unauthorized => Unauthorized,
        StatusCodes.Status404NotFound => NotFound,
        StatusCodes.Status429TooManyRequests => RateLimited,
        >= 500 => InternalError,
        _ => InvalidRequest
    };

    private static string DetailFor(int status) => status switch
    {
        StatusCodes.Status400BadRequest => "The request could not be read: the body is not valid JSON, or a value has the wrong type.",
        StatusCodes.Status401Unauthorized => "A valid API key is required.",
        StatusCodes.Status404NotFound => "The resource does not exist.",
        StatusCodes.Status405MethodNotAllowed => "This method is not allowed on this resource.",
        StatusCodes.Status415UnsupportedMediaType => "The request body must be application/json.",
        StatusCodes.Status429TooManyRequests => "Too many requests.",
        >= 500 => "The request failed unexpectedly.",
        _ => "The request was refused."
    };
}
