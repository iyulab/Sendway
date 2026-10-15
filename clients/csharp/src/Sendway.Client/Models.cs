using System.Text.Json.Serialization;

namespace Sendway.Client;

/// <summary>An email to send. All recipients share one envelope: one message, one outcome.</summary>
/// <param name="To">At least one address.</param>
/// <param name="Subject">At most 200 characters.</param>
/// <param name="Body">The plain-text body; the fallback part when <see cref="HtmlBody"/> is set.</param>
public sealed record EmailMessage(IReadOnlyList<string> To, string Subject, string Body)
{
    /// <summary>Copy recipients.</summary>
    public IReadOnlyList<string>? Cc { get; init; }

    /// <summary>Blind copy recipients.</summary>
    public IReadOnlyList<string>? Bcc { get; init; }

    /// <summary>A complete HTML body, sent with <see cref="Body"/> as its plain-text alternative.</summary>
    public string? HtmlBody { get; init; }

    /// <summary>A message still unsent at this time is not sent (<see cref="FailureReason.Expired"/>).</summary>
    public DateTimeOffset? ExpiresAt { get; init; }
}

/// <summary>An app push notification to send through Firebase Cloud Messaging.</summary>
public sealed record PushMessage(string DeviceToken, string Title, string Body)
{
    /// <summary>
    /// A message still unsent at this time is not sent; it is also handed to the platforms, so a device that comes
    /// online later does not show it.
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; init; }
}

/// <summary>An SMS to send. Informational messages only.</summary>
/// <param name="To">One number in E.164 form, e.g. <c>+821012345678</c>.</param>
public sealed record SmsMessage(string To, string Body)
{
    /// <summary>A message still unsent at this time is not sent (<see cref="FailureReason.Expired"/>).</summary>
    public DateTimeOffset? ExpiresAt { get; init; }
}

/// <summary>A message Sendway accepted for delivery. Delivery happens afterwards; read the outcome with
/// <see cref="SendwayClient.GetMessageAsync"/> or receive it as a webhook.</summary>
public sealed record AcceptedMessage(Guid Id, [property: JsonPropertyName("status")] string StatusText)
{
    /// <summary>The status when the answer was given: <see cref="MessageState.Queued"/> for a new message, the
    /// current one when the same <c>Idempotency-Key</c> was used before.</summary>
    [JsonIgnore]
    public MessageState Status => Names.State(StatusText);
}

/// <summary>Where a message is in its delivery, and how it ended once it has.</summary>
public sealed record Message(
    Guid Id,
    string Channel,
    string Recipient,
    [property: JsonPropertyName("status")] string StatusText,
    string? Error,
    [property: JsonPropertyName("reason")] string? ReasonText,
    IReadOnlyList<RefusedRecipient>? RefusedRecipients,
    int Attempts,
    DateTimeOffset AcceptedAt,
    DateTimeOffset? CompletedAt)
{
    /// <summary>The delivery state. <see cref="MessageState.Sent"/> and <see cref="MessageState.Failed"/> are final.</summary>
    [JsonIgnore]
    public MessageState Status => Names.State(StatusText);

    /// <summary>Why it ended <see cref="MessageState.Failed"/>; null otherwise. Branch on this, not on <see cref="Error"/>.</summary>
    [JsonIgnore]
    public FailureReason? Reason => ReasonText is null ? null : Names.Reason(ReasonText);
}

/// <summary>An email recipient the mail server refused for good while it accepted the others, with its reply.</summary>
public sealed record RefusedRecipient(string Address, string Error);

/// <summary>Where a message is in its delivery.</summary>
public enum MessageState
{
    /// <summary>A state this version of the client does not know; <see cref="Message.StatusText"/> has it as sent.</summary>
    Unknown,

    /// <summary>Waiting for its first attempt, or for a retry.</summary>
    Queued,

    /// <summary>An attempt is in progress.</summary>
    Sending,

    /// <summary>Final: the provider took the message.</summary>
    Sent,

    /// <summary>Final: it will not be sent. See <see cref="Message.Reason"/>.</summary>
    Failed
}

/// <summary>Why a message ended <see cref="MessageState.Failed"/>, and so who can act on it.</summary>
public enum FailureReason
{
    /// <summary>A reason this version of the client does not know; <see cref="Message.ReasonText"/> has it as sent.</summary>
    Unknown,

    /// <summary>The destination refused or does not exist, or opted out. Fix or stop using the destination.</summary>
    RecipientRefused,

    /// <summary>The provider refused the message itself (content, size, form). Sending it again fails the same way.</summary>
    MessageRefused,

    /// <summary>The destination already got as many messages as the provider allows in a short window.</summary>
    RecipientRateLimited,

    /// <summary>The message's deadline passed before it could be sent.</summary>
    Expired,

    /// <summary>A failure that could have cleared up lasted through every attempt — for whoever runs the deployment.</summary>
    AttemptsExhausted
}

internal static class Names
{
    public static MessageState State(string? text) => text switch
    {
        "Queued" => MessageState.Queued,
        "Sending" => MessageState.Sending,
        "Sent" => MessageState.Sent,
        "Failed" => MessageState.Failed,
        _ => MessageState.Unknown
    };

    public static FailureReason Reason(string text) => text switch
    {
        "recipient_refused" => FailureReason.RecipientRefused,
        "message_refused" => FailureReason.MessageRefused,
        "recipient_rate_limited" => FailureReason.RecipientRateLimited,
        "expired" => FailureReason.Expired,
        "attempts_exhausted" => FailureReason.AttemptsExhausted,
        _ => FailureReason.Unknown
    };

    public static SendwayErrorCode Code(string? text) => text switch
    {
        "invalid_request" => SendwayErrorCode.InvalidRequest,
        "invalid_recipient" => SendwayErrorCode.InvalidRecipient,
        "idempotency_key_reused" => SendwayErrorCode.IdempotencyKeyReused,
        "channel_not_configured" => SendwayErrorCode.ChannelNotConfigured,
        "rate_limited" => SendwayErrorCode.RateLimited,
        "unauthorized" => SendwayErrorCode.Unauthorized,
        "not_found" => SendwayErrorCode.NotFound,
        "internal_error" => SendwayErrorCode.InternalError,
        _ => SendwayErrorCode.Unknown
    };
}
