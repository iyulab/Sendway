namespace Sendway.Core;

public enum VerificationStatus
{
    // A code was sent and has not been used up.
    Pending,

    // The right code was entered. A code works once.
    Verified,

    // A newer code for the same destination and purpose was started; only the latest code works.
    Superseded
}

// One "prove you hold this address or number" attempt: Sendway made the code, sent it through the ordinary
// message path, and keeps it (protected) until it is used or stops mattering. What the proof means for the
// calling service — whose account, whether to let anyone in — is that service's to decide; Sendway only knows
// that whoever entered the code received it at the destination.
public sealed class Verification
{
    public required Guid Id { get; init; }

    public required Guid TenantId { get; init; }

    public required string Channel { get; init; }

    // The destination as the per-destination limit counts it (MessageSubmission.DestinationKey).
    public required string Destination { get; init; }

    // An opaque label from the caller ("signup", "withdrawal"): codes for different purposes never mix.
    public required string Purpose { get; init; }

    public VerificationStatus Status { get; set; }

    // The code, protected at rest; cleared as soon as the verification is Verified, Superseded or out of
    // attempts. Null afterwards.
    public string? ProtectedCode { get; set; }

    public required int MaxAttempts { get; init; }

    // Counted before the code is compared, so parallel guesses cannot get more tries than MaxAttempts.
    public int Attempts { get; set; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }

    public DateTimeOffset? VerifiedAt { get; set; }

    // The message that carried the code, so the caller can see whether it was delivered.
    public Guid? MessageId { get; set; }

    public string? IdempotencyKey { get; init; }

    // Fingerprint of the request the key was first used with.
    public string? RequestHash { get; init; }
}
