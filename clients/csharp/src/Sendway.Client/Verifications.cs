using System.Text.Json.Serialization;

namespace Sendway.Client;

/// <summary>The channels a verification code can go by.</summary>
public static class VerificationChannel
{
    /// <summary>A text message to a phone number (E.164, e.g. <c>+821012345678</c>).</summary>
    public const string Sms = "sms";

    /// <summary>An email to an address.</summary>
    public const string Email = "email";
}

/// <summary>
/// A request to send a verification code to an address or number, to learn that whoever enters it holds that
/// destination. Sendway makes the code and the wording; what a successful check means for your service (whose
/// account, whether to let anyone in) is yours to decide.
/// </summary>
/// <param name="Channel"><see cref="VerificationChannel.Sms"/> or <see cref="VerificationChannel.Email"/>.</param>
/// <param name="To">The address, or the number in E.164 form.</param>
public sealed record VerificationRequest(string Channel, string To)
{
    /// <summary>A label of yours ("signup", "withdrawal"; at most 64 characters): codes for different purposes never mix.</summary>
    public string? Purpose { get; init; }

    /// <summary>How long the code works: 30 to 900 seconds, 300 by default.</summary>
    public int? TtlSeconds { get; init; }

    /// <summary>Digits in the code: 4 to 8, 6 by default. A short code is easier to guess; see the service documentation.</summary>
    public int? CodeLength { get; init; }

    /// <summary>Wrong codes allowed before the code is dead: 1 to 10, 5 by default.</summary>
    public int? MaxAttempts { get; init; }

    /// <summary>The wording of the message: <c>en</c> (default) or <c>ko</c>.</summary>
    public string? Locale { get; init; }
}

/// <summary>A verification Sendway started: the code went to the destination.</summary>
/// <param name="Code">Set only on a sandbox tenant, whose messages are not delivered; a real tenant never gets the code.</param>
public sealed record StartedVerification(Guid Id, DateTimeOffset ExpiresAt, string? Code);

/// <summary>The answer to checking a code.</summary>
public sealed record VerificationCheck(
    [property: JsonPropertyName("status")] string StatusText,
    int? AttemptsLeft)
{
    /// <summary>What the check found. Only <see cref="VerificationResult.Verified"/> proves anything.</summary>
    [JsonIgnore]
    public VerificationResult Result => Names.CheckResult(StatusText);
}

/// <summary>What checking a code found.</summary>
public enum VerificationResult
{
    /// <summary>A result this version of the client does not know; <see cref="VerificationCheck.StatusText"/> has it as sent.</summary>
    Unknown,

    /// <summary>The right code. A code works once.</summary>
    Verified,

    /// <summary>The wrong code; <see cref="VerificationCheck.AttemptsLeft"/> tries remain.</summary>
    Mismatch,

    /// <summary>Out of time, already used, or replaced by a newer code.</summary>
    Expired,

    /// <summary>The tries are used up; the code no longer works whatever is entered.</summary>
    TooManyAttempts
}

/// <summary>Where a verification stands, and how the message that carried its code ended.</summary>
public sealed record Verification(
    Guid Id,
    string Channel,
    string Purpose,
    [property: JsonPropertyName("status")] string StatusText,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    int AttemptsLeft,
    VerificationDelivery Delivery)
{
    /// <summary>The verification state.</summary>
    [JsonIgnore]
    public VerificationState State => Names.VerificationState(StatusText);
}

/// <summary>How the message that carried a verification code ended. Do not show this to the person being verified:
/// it tells them whether a number exists.</summary>
public sealed record VerificationDelivery(
    [property: JsonPropertyName("status")] string? StatusText,
    [property: JsonPropertyName("reason")] string? ReasonText)
{
    /// <summary>The delivery state of the message; null if it is not known.</summary>
    [JsonIgnore]
    public MessageState? Status => StatusText is null ? null : Names.State(StatusText);

    /// <summary>Why the message failed; null otherwise.</summary>
    [JsonIgnore]
    public FailureReason? Reason => ReasonText is null ? null : Names.Reason(ReasonText);
}

/// <summary>Where a verification is in its life.</summary>
public enum VerificationState
{
    /// <summary>A state this version of the client does not know; <see cref="Verification.StatusText"/> has it as sent.</summary>
    Unknown,

    /// <summary>A code was sent and can still be checked.</summary>
    Pending,

    /// <summary>The right code was entered.</summary>
    Verified,

    /// <summary>Its time passed.</summary>
    Expired,

    /// <summary>The tries are used up.</summary>
    TooManyAttempts,

    /// <summary>A newer code for the same destination and purpose replaced it.</summary>
    Superseded
}
