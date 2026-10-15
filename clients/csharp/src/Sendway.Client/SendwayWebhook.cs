using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sendway.Client;

/// <summary>
/// Reads an outcome webhook: checks its Standard Webhooks signature against the endpoint's secret, then reads the event.
/// Verify on the raw request body, before any JSON parsing or re-serialization changes a byte of it.
/// </summary>
public sealed class SendwayWebhook
{
    /// <summary>The header carrying the event id. The same on every redelivery: use it to ignore duplicates.</summary>
    public const string IdHeader = "webhook-id";

    /// <summary>The header carrying the Unix time the delivery was signed.</summary>
    public const string TimestampHeader = "webhook-timestamp";

    /// <summary>The header carrying the signatures (<c>v1,…</c>, space-separated).</summary>
    public const string SignatureHeader = "webhook-signature";

    private const string SecretPrefix = "whsec_";

    private readonly byte[] key;
    private readonly TimeProvider timeProvider;

    /// <param name="secret">The endpoint's signing secret as Sendway gave it (<c>whsec_…</c>).</param>
    /// <param name="tolerance">How far the signed time may be from now; 5 minutes when null. Bounds replays.</param>
    public SendwayWebhook(string secret, TimeSpan? tolerance = null, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);
        var encoded = secret.StartsWith(SecretPrefix, StringComparison.Ordinal) ? secret[SecretPrefix.Length..] : secret;
        try
        {
            key = Convert.FromBase64String(encoded);
        }
        catch (FormatException ex)
        {
            throw new ArgumentException("The secret is not a Standard Webhooks secret (whsec_ followed by base64).", nameof(secret), ex);
        }

        Tolerance = tolerance ?? TimeSpan.FromMinutes(5);
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>How far the signed time may be from now.</summary>
    public TimeSpan Tolerance { get; }

    /// <summary>Verifies a delivery and reads its event.</summary>
    /// <exception cref="WebhookVerificationException">The delivery is not signed with this secret, or is too old or too new.</exception>
    public MessageEvent Verify(string id, string timestamp, string signature, string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(timestamp) || string.IsNullOrEmpty(signature))
        {
            throw new WebhookVerificationException("The webhook-id, webhook-timestamp and webhook-signature headers are all required.");
        }

        if (!long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
        {
            throw new WebhookVerificationException("The webhook-timestamp header is not a Unix time.");
        }

        var signedAt = DateTimeOffset.FromUnixTimeSeconds(seconds);
        if ((timeProvider.GetUtcNow() - signedAt).Duration() > Tolerance)
        {
            throw new WebhookVerificationException("The delivery was signed too long ago, or in the future.");
        }

        var expected = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes($"{id}.{timestamp}.{body}"));
        if (!signature.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(candidate => Matches(candidate, expected)))
        {
            throw new WebhookVerificationException("No signature matches this secret.");
        }

        return JsonSerializer.Deserialize(body, ClientJsonContext.Default.MessageEvent)
            ?? throw new WebhookVerificationException("The body is not an event.");
    }

    private static bool Matches(string candidate, byte[] expected)
    {
        if (!candidate.StartsWith("v1,", StringComparison.Ordinal))
        {
            return false;
        }

        Span<byte> actual = stackalloc byte[64];
        return Convert.TryFromBase64String(candidate[3..], actual, out var written)
            && CryptographicOperations.FixedTimeEquals(actual[..written], expected);
    }
}

/// <summary>An outcome webhook: a message reached <see cref="MessageState.Sent"/> or <see cref="MessageState.Failed"/>.</summary>
public sealed record MessageEvent([property: JsonPropertyName("type")] string TypeText, DateTimeOffset Timestamp, Message Data)
{
    /// <summary>The event type <c>message.sent</c>.</summary>
    public const string Sent = "message.sent";

    /// <summary>The event type <c>message.failed</c>.</summary>
    public const string Failed = "message.failed";
}

/// <summary>An outcome webhook that did not pass verification. Answer it with an error and do not act on it.</summary>
public sealed class WebhookVerificationException : Exception
{
    public WebhookVerificationException()
    {
    }

    public WebhookVerificationException(string message)
        : base(message)
    {
    }

    public WebhookVerificationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
