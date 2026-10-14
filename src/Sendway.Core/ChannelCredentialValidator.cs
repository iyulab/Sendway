using System.Text.Json;

namespace Sendway.Core;

// Checks a channel credential for what its sender needs before it is stored. Sending is done in the
// background, so a credential that could never work would otherwise surface only later, as messages
// failing one after another. Each method returns what is wrong, or null when nothing is.
//
// This is a completeness check, not proof the credential works: whether the provider accepts it is
// only known when a message is sent.
public static class ChannelCredentialValidator
{
    public static string? Validate(SmtpOptions options)
    {
        if (!IsEmailAddress(options.FromAddress))
        {
            return "fromAddress must be a valid email address.";
        }

        if (string.IsNullOrWhiteSpace(options.Host) && options.Provider is null)
        {
            return "Either host or provider must be set.";
        }

        if (options.Port is < 1 or > 65535)
        {
            return "port must be between 1 and 65535.";
        }

        if (!string.IsNullOrEmpty(options.Username) && string.IsNullOrEmpty(options.Password))
        {
            return "password is required when username is set.";
        }

        if (options.MaxConnections is < 1)
        {
            return "maxConnections must be at least 1.";
        }

        return null;
    }

    public static string? Validate(GraphOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.DirectoryId))
        {
            return "directoryId is required.";
        }

        if (string.IsNullOrWhiteSpace(options.ClientId))
        {
            return "clientId is required.";
        }

        if (string.IsNullOrWhiteSpace(options.ClientSecret))
        {
            return "clientSecret is required.";
        }

        if (!IsEmailAddress(options.FromAddress))
        {
            return "fromAddress must be a valid email address.";
        }

        return null;
    }

    public static string? Validate(FcmOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.CredentialsJson))
        {
            return "credentialsJson is required.";
        }

        try
        {
            using var document = JsonDocument.Parse(options.CredentialsJson);
            var root = document.RootElement;

            // The fields a service-account key file always carries, and without which no access
            // token can be requested.
            if (root.ValueKind != JsonValueKind.Object ||
                !HasText(root, "client_email") ||
                !HasText(root, "private_key"))
            {
                return "credentialsJson must be a service account key (a JSON object with client_email and private_key).";
            }
        }
        catch (JsonException)
        {
            return "credentialsJson must be valid JSON.";
        }

        return null;
    }

    public static string? Validate(SolapiOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            return "apiKey is required.";
        }

        if (string.IsNullOrWhiteSpace(options.ApiSecret))
        {
            return "apiSecret is required.";
        }

        // The sender number as registered with the provider: digits only, no hyphens or country code.
        if (string.IsNullOrEmpty(options.From) || options.From.Length > 15 || !options.From.All(char.IsAsciiDigit))
        {
            return "from must be the registered sender number, digits only (e.g. 0212345678).";
        }

        return null;
    }

    private static bool HasText(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString());

    private static bool IsEmailAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return false;
        }

        try
        {
            EmailAddressValidator.Validate(address);
            return true;
        }
        catch (InvalidRecipientException)
        {
            return false;
        }
    }
}
