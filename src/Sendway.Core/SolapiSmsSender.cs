using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sendway.Core;

// Sends SMS through SOLAPI (https://solapi.com), one message per request. The credential is read on
// every send, so a replaced credential applies to the next attempt without any cache to clear.
//
// A send succeeds when SOLAPI accepts the message for delivery to the carrier. Whether the handset
// received it is reported by the carrier later and is not tracked here.
public sealed class SolapiSmsSender : ISmsSender
{
    public static readonly Uri DefaultBaseAddress = new("https://api.solapi.com/");

    private const string KoreaCountryCode = "82";

    // SOLAPI counts message length in EUC-KR bytes: a domestic message may be up to 2000 bytes (it
    // goes out as a long message past 90), a message abroad only 90 — longer ones are refused.
    private const int MaxDomesticBytes = 2000;
    private const int MaxInternationalBytes = 90;

    // Destinations SOLAPI stopped serving: +1 and +65 since 2024-11-08, +86 since 2025-12-05. The
    // provider changes this list; it is kept here so such messages are refused when accepted.
    private static readonly HashSet<string> UnservedCountryCodes = ["1", "65", "86"];

    // Codes meaning SOLAPI refused this message for what it is — its content, or a recipient who
    // opted out — so another attempt would be refused the same way. Any other refusal (balance,
    // sender number, credentials, a server error) can clear up, and the message is attempted again.
    private static readonly HashSet<string> MessageRejections =
    [
        "1010", "1011", "1028", "1029", "1031", "1061", "1065",
        "2011", "2061", "2065", "2254"
    ];

    private static readonly Encoding EucKr = CreateEucKr();

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _httpClient;
    private readonly ICredentialStore _credentialStore;
    private readonly TimeProvider _timeProvider;

    public SolapiSmsSender(HttpClient httpClient, ICredentialStore credentialStore, TimeProvider timeProvider)
    {
        _httpClient = httpClient;
        _credentialStore = credentialStore;
        _timeProvider = timeProvider;
    }

    public string? Check(SmsMessage message)
    {
        if (UnservedCountryCodes.Contains(message.To.CountryCode))
        {
            return $"SMS to +{message.To.CountryCode} numbers is not available.";
        }

        int length;
        try
        {
            length = EucKr.GetByteCount(message.Body);
        }
        catch (EncoderFallbackException)
        {
            return "body contains characters an SMS cannot carry (emoji and other characters outside EUC-KR).";
        }

        var limit = IsDomestic(message.To) ? MaxDomesticBytes : MaxInternationalBytes;
        return length > limit
            ? $"body must not exceed {limit} bytes for this destination (a Korean character counts as 2)."
            : null;
    }

    public async Task SendAsync(SmsMessage message, CancellationToken cancellationToken = default)
    {
        var options = await _credentialStore.GetAsync<SolapiOptions>(message.TenantId, ChannelCredentialNames.Solapi, cancellationToken)
            ?? throw new InvalidOperationException("Solapi channel credentials have not been configured.");

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(BaseAddress, "messages/v4/send-many/detail"))
        {
            Content = JsonContent.Create(new SendRequest([ToSolapiMessage(message, options.From)]), options: JsonOptions)
        };
        request.Headers.TryAddWithoutValidation("Authorization", Authorization(options));

        using var response = await _httpClient.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var error = await ReadAsync<ErrorResponse>(response, cancellationToken);
            throw new InvalidOperationException(
                $"Solapi answered {(int)response.StatusCode}: {error?.ErrorCode} {error?.ErrorMessage}".TrimEnd());
        }

        var result = await ReadAsync<SendResponse>(response, cancellationToken);
        if (result?.FailedMessageList is [var failed, ..])
        {
            var reason = $"Solapi refused the message: {failed.StatusCode} {failed.StatusMessage}".TrimEnd();
            throw MessageRejections.Contains(failed.StatusCode ?? "")
                ? new MessageRejectedException(reason)
                : new InvalidOperationException(reason);
        }
    }

    private Uri BaseAddress => _httpClient.BaseAddress ?? DefaultBaseAddress;

    private static bool IsDomestic(PhoneNumber number) => number.CountryCode == KoreaCountryCode;

    // A domestic number is written the way it is dialed inside Korea (with the leading 0) and its
    // type is left to SOLAPI (SMS up to 90 bytes, a long message past that). A number abroad is sent
    // as an SMS, the only type SOLAPI delivers abroad.
    private static SolapiMessage ToSolapiMessage(SmsMessage message, string from) => IsDomestic(message.To)
        ? new SolapiMessage("0" + message.To.NationalNumber, from, message.Body, KoreaCountryCode, Type: null, AutoTypeDetect: true)
        : new SolapiMessage(message.To.NationalNumber, from, message.Body, message.To.CountryCode, Type: "SMS", AutoTypeDetect: null);

    // HMAC-SHA256 over the request time and a random salt, keyed with the API secret. SOLAPI accepts
    // a time within 15 minutes of its own and refuses a signature it has seen in that window.
    private string Authorization(SolapiOptions options)
    {
        var date = _timeProvider.GetUtcNow().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        var salt = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var signature = Convert.ToHexStringLower(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(options.ApiSecret), Encoding.UTF8.GetBytes(date + salt)));

        return $"HMAC-SHA256 apiKey={options.ApiKey}, date={date}, salt={salt}, signature={signature}";
    }

    private static async Task<T?> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken) where T : class
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Encoding CreateEucKr()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(51949, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }

    private sealed record SendRequest(IReadOnlyList<SolapiMessage> Messages);

    private sealed record SolapiMessage(string To, string From, string Text, string Country, string? Type, bool? AutoTypeDetect);

    private sealed record SendResponse(IReadOnlyList<FailedMessage>? FailedMessageList);

    private sealed record FailedMessage(string? StatusCode, string? StatusMessage);

    private sealed record ErrorResponse(string? ErrorCode, string? ErrorMessage);
}
