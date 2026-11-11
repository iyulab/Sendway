using Microsoft.Extensions.Options;
using Sendway.Core;
using Sendway.Service.Auth;

namespace Sendway.Service.Endpoints;

// Channel: "sms" or "email". Purpose: an opaque label from the caller; codes for different purposes never mix.
public sealed record StartVerificationRequest(
    string? Channel, string? To, string? Purpose = null, int? TtlSeconds = null, int? CodeLength = null, int? MaxAttempts = null, string? Locale = null);

// Code: present only for a sandbox tenant, whose messages are not delivered — a real tenant never sees the code.
public sealed record StartVerificationResponse(Guid Id, DateTimeOffset ExpiresAt, string? Code = null);

public sealed record CheckVerificationRequest(string? Code);

// Status: verified | mismatch | expired | too_many_attempts. AttemptsLeft only with mismatch.
public sealed record CheckVerificationResponse(string Status, int? AttemptsLeft = null);

// Status: pending | verified | expired | too_many_attempts | superseded. Delivery: how the message carrying the code ended.
public sealed record VerificationStatusResponse(
    Guid Id, string Channel, string Purpose, string Status, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, int AttemptsLeft, VerificationDelivery Delivery);

public sealed record VerificationDelivery(string? Status, string? Reason);

// "Does whoever is on the other end hold this address or number?" — Sendway makes a code, sends it, and checks
// what comes back. What a successful check means for the calling service (whose account, whether to let anyone
// in) is not Sendway's to know.
public static class VerificationEndpoints
{
    private const int MaxIdempotencyKeyLength = 255;
    private const int MaxCodeInputLength = 32;

    public static void MapVerificationEndpoints(this IEndpointRouteBuilder group)
    {
        group.MapPost("/", async (StartVerificationRequest request, HttpContext httpContext, VerificationService verifications, ISmsSender smsSender) =>
        {
            if (request.Channel is not (MessageChannels.Sms or MessageChannels.Email))
            {
                return ApiProblem.BadRequest("channel must be \"sms\" or \"email\".");
            }

            if (string.IsNullOrWhiteSpace(request.To))
            {
                return ApiProblem.BadRequest("to is required.");
            }

            var purpose = request.Purpose ?? string.Empty;
            if (purpose.Length > VerificationLimits.MaxPurposeLength)
            {
                return ApiProblem.BadRequest($"purpose must not exceed {VerificationLimits.MaxPurposeLength} characters.");
            }

            var ttl = TimeSpan.FromSeconds(request.TtlSeconds ?? VerificationLimits.DefaultTtl.TotalSeconds);
            if (ttl < VerificationLimits.MinTtl || ttl > VerificationLimits.MaxTtl)
            {
                return ApiProblem.BadRequest(
                    $"ttlSeconds must be between {VerificationLimits.MinTtl.TotalSeconds:0} and {VerificationLimits.MaxTtl.TotalSeconds:0}.");
            }

            var codeLength = request.CodeLength ?? VerificationLimits.DefaultCodeLength;
            if (codeLength is < VerificationLimits.MinCodeLength or > VerificationLimits.MaxCodeLength)
            {
                return ApiProblem.BadRequest($"codeLength must be between {VerificationLimits.MinCodeLength} and {VerificationLimits.MaxCodeLength}.");
            }

            var maxAttempts = request.MaxAttempts ?? VerificationLimits.DefaultAttempts;
            if (maxAttempts is < VerificationLimits.MinAttempts or > VerificationLimits.MaxAttemptsAllowed)
            {
                return ApiProblem.BadRequest($"maxAttempts must be between {VerificationLimits.MinAttempts} and {VerificationLimits.MaxAttemptsAllowed}.");
            }

            var locale = request.Locale ?? "en";
            if (!VerificationLimits.Locales.Contains(locale))
            {
                return ApiProblem.BadRequest($"locale must be one of: {string.Join(", ", VerificationLimits.Locales)}.");
            }

            string? idempotencyKey = null;
            if (httpContext.Request.Headers.TryGetValue("Idempotency-Key", out var keyValues) && !string.IsNullOrWhiteSpace(keyValues))
            {
                idempotencyKey = keyValues.ToString();
                if (idempotencyKey.Length > MaxIdempotencyKeyLength)
                {
                    return ApiProblem.BadRequest($"Idempotency-Key must not exceed {MaxIdempotencyKeyLength} characters.");
                }
            }

            var tenant = httpContext.GetTenant();
            var limits = httpContext.RequestServices.GetRequiredService<IOptions<TenantLimitOptions>>().Value;

            var start = new VerificationStart(
                tenant.Id, request.Channel, request.To, purpose, ttl, codeLength, maxAttempts, locale, idempotencyKey, tenant.Sandbox,
                TenantLimits.PerDay(tenant, request.Channel, limits),
                TenantLimits.PerDestinationPerHour(tenant, request.Channel, limits),
                TenantLimits.DestinationCooldown(tenant, limits));

            if (request.Channel == MessageChannels.Sms)
            {
                // The same refusals as sending an SMS, known now rather than reported as a failed delivery later.
                if (await SmsRefusalAsync(httpContext, smsSender, start, limits) is { } refusal)
                {
                    return refusal;
                }
            }

            VerificationStartResult result;
            try
            {
                result = await verifications.StartAsync(start);
            }
            catch (InvalidRecipientException ex)
            {
                return ApiProblem.BadRequest(ex.Message, ApiProblem.InvalidRecipient);
            }

            switch (result.Outcome)
            {
                case VerificationStartOutcome.Started or VerificationStartOutcome.Replayed:
                    var verification = result.Verification!;
                    return Results.Accepted($"/verifications/{verification.Id}", new StartVerificationResponse(verification.Id, verification.ExpiresAt, result.Code));
                case VerificationStartOutcome.KeyReused:
                    return ApiProblem.Create(
                        StatusCodes.Status422UnprocessableEntity,
                        ApiProblem.IdempotencyKeyReused,
                        "This Idempotency-Key was already used for a different request.");
                case VerificationStartOutcome.ChannelNotConfigured:
                    return MessageAcceptance.ChannelNotConfigured(request.Channel);
                case VerificationStartOutcome.DailyLimitReached:
                    return MessageAcceptance.DailyLimitReached(httpContext, request.Channel, result.RetryAfter!.Value);
                case VerificationStartOutcome.ResendTooSoon:
                    return MessageAcceptance.DestinationLimited(
                        httpContext, request.Channel, result.RetryAfter!.Value,
                        "A code was sent to this destination a moment ago. Nothing was sent; try again after Retry-After. The code already sent still works until it expires.");
                default:
                    return MessageAcceptance.DestinationLimited(httpContext, request.Channel, result.RetryAfter!.Value);
            }
        })
        .WithName("StartVerification")
        .WithSummary("Send a verification code to an email address or phone number")
        .WithTags("Verifications")
        .Produces<StartVerificationResponse>(StatusCodes.Status202Accepted)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
        .ProducesProblem(StatusCodes.Status429TooManyRequests);

        group.MapPost("/{id:guid}/check", async (Guid id, CheckVerificationRequest request, HttpContext httpContext, VerificationService verifications) =>
        {
            if (string.IsNullOrWhiteSpace(request.Code) || request.Code.Length > MaxCodeInputLength)
            {
                return ApiProblem.BadRequest("code is required.");
            }

            var result = await verifications.CheckAsync(httpContext.GetTenant().Id, id, request.Code);
            return result.Outcome switch
            {
                VerificationCheckOutcome.NotFound => Results.NotFound(),
                VerificationCheckOutcome.Verified => Results.Ok(new CheckVerificationResponse("verified")),
                VerificationCheckOutcome.Mismatch => Results.Ok(new CheckVerificationResponse("mismatch", result.AttemptsLeft)),
                VerificationCheckOutcome.TooManyAttempts => Results.Ok(new CheckVerificationResponse("too_many_attempts")),
                _ => Results.Ok(new CheckVerificationResponse("expired"))
            };
        })
        .WithName("CheckVerification")
        .WithSummary("Check the code a person entered")
        .WithTags("Verifications")
        .Produces<CheckVerificationResponse>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status429TooManyRequests);

        group.MapGet("/{id:guid}", async (Guid id, HttpContext httpContext, VerificationService verifications) =>
        {
            var view = await verifications.GetAsync(httpContext.GetTenant().Id, id);
            if (view is null)
            {
                return Results.NotFound();
            }

            var v = view.Verification;
            return Results.Ok(new VerificationStatusResponse(
                v.Id, v.Channel, v.Purpose, view.State, v.CreatedAt, v.ExpiresAt,
                Math.Max(0, v.MaxAttempts - v.Attempts),
                new VerificationDelivery(view.DeliveryStatus, view.DeliveryReason)));
        })
        .WithName("GetVerification")
        .WithSummary("Read where a verification stands and how its message ended")
        .WithTags("Verifications")
        .Produces<VerificationStatusResponse>()
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status429TooManyRequests);
    }

    // Mirrors SendSmsEndpoint: the tenant's allowed countries, then the provider's own check on a message of the
    // same size as the one that will be sent.
    private static async Task<IResult?> SmsRefusalAsync(HttpContext httpContext, ISmsSender smsSender, VerificationStart start, TenantLimitOptions limits)
    {
        SmsMessage sample;
        try
        {
            sample = new SmsMessage(start.TenantId, start.To, VerificationService.BodyFor(start.Locale, new string('0', start.CodeLength), start.Ttl).Body);
        }
        catch (InvalidRecipientException ex)
        {
            return ApiProblem.BadRequest(ex.Message, ApiProblem.InvalidRecipient);
        }

        if (!TenantLimits.SmsCountries(httpContext.GetTenant(), limits).Contains(sample.To.CountryCode))
        {
            return ApiProblem.BadRequest(
                $"This service may not send SMS to +{sample.To.CountryCode} numbers. Ask an administrator to allow the country.",
                ApiProblem.DestinationNotAllowed);
        }

        return await Task.FromResult(smsSender.Check(sample) is { } problem ? ApiProblem.BadRequest(problem) : null);
    }
}
