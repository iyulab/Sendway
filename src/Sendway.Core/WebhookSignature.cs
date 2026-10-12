using System.Security.Cryptography;
using System.Text;

namespace Sendway.Core;

// The symmetric signature scheme of the Standard Webhooks specification, so receivers can verify
// deliveries with any existing implementation of it: HMAC-SHA256 over "{id}.{timestamp}.{body}",
// keyed with the base64-decoded secret, sent as "v1,{base64 signature}".
public static class WebhookSignature
{
    public const string SecretPrefix = "whsec_";

    public const string IdHeader = "webhook-id";
    public const string TimestampHeader = "webhook-timestamp";
    public const string SignatureHeader = "webhook-signature";

    public static string Compute(string secret, string id, long unixTimeSeconds, string body)
    {
        var encodedKey = secret.StartsWith(SecretPrefix, StringComparison.Ordinal) ? secret[SecretPrefix.Length..] : secret;
        var key = Convert.FromBase64String(encodedKey);
        var signedContent = Encoding.UTF8.GetBytes($"{id}.{unixTimeSeconds}.{body}");

        return "v1," + Convert.ToBase64String(HMACSHA256.HashData(key, signedContent));
    }
}
