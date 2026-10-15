using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Sendway.Client;

/// <summary>
/// Sendway's API for a calling service: send email, SMS and app push, and read a message's outcome. A send is accepted
/// (it returns once Sendway has stored the message) and delivered afterwards.
/// </summary>
public sealed class SendwayClient
{
    private readonly HttpClient http;
    private readonly Uri root;
    private readonly string apiKey;

    /// <param name="httpClient">A client whose <see cref="HttpClient.BaseAddress"/> is the Sendway instance.</param>
    /// <param name="apiKey">The calling service's tenant key.</param>
    public SendwayClient(HttpClient httpClient, string apiKey)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentException.ThrowIfNullOrEmpty(apiKey);
        if (httpClient.BaseAddress is null)
        {
            throw new ArgumentException("The client needs the Sendway instance as its base address.", nameof(httpClient));
        }

        http = httpClient;
        var address = httpClient.BaseAddress.ToString();
        root = new Uri(address.EndsWith('/') ? address : address + "/");
        this.apiKey = apiKey;
    }

    /// <summary>Accepts an email for delivery.</summary>
    /// <param name="idempotencyKey">
    /// Recommended. Calling again with the same key and the same message, within 24 hours, answers with the original
    /// message instead of sending a second one — so a call whose answer was lost can be repeated safely.
    /// </param>
    public Task<AcceptedMessage> SendEmailAsync(EmailMessage message, string? idempotencyKey = null, CancellationToken cancellationToken = default) =>
        SendAsync("messages/email", message, ClientJsonContext.Default.EmailMessage, idempotencyKey, cancellationToken);

    /// <summary>Accepts an app push notification for delivery.</summary>
    /// <param name="idempotencyKey">See <see cref="SendEmailAsync"/>.</param>
    public Task<AcceptedMessage> SendPushAsync(PushMessage message, string? idempotencyKey = null, CancellationToken cancellationToken = default) =>
        SendAsync("messages/push", message, ClientJsonContext.Default.PushMessage, idempotencyKey, cancellationToken);

    /// <summary>Accepts an SMS for delivery.</summary>
    /// <param name="idempotencyKey">See <see cref="SendEmailAsync"/>.</param>
    public Task<AcceptedMessage> SendSmsAsync(SmsMessage message, string? idempotencyKey = null, CancellationToken cancellationToken = default) =>
        SendAsync("messages/sms", message, ClientJsonContext.Default.SmsMessage, idempotencyKey, cancellationToken);

    /// <summary>Reads a message's status and, once final, its outcome.</summary>
    public async Task<Message> GetMessageAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(root, "messages/" + id.ToString("D", CultureInfo.InvariantCulture)));
        using var response = await CallAsync(request, cancellationToken).ConfigureAwait(false);
        return await ReadAsync(response, ClientJsonContext.Default.Message, cancellationToken).ConfigureAwait(false);
    }

    private async Task<AcceptedMessage> SendAsync<T>(
        string path, T message, JsonTypeInfo<T> type, string? idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(root, path))
        {
            Content = new StringContent(JsonSerializer.Serialize(message, type), Encoding.UTF8, "application/json")
        };
        if (!string.IsNullOrEmpty(idempotencyKey))
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        using var response = await CallAsync(request, cancellationToken).ConfigureAwait(false);
        return await ReadAsync(response, ClientJsonContext.Default.AcceptedMessage, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> CallAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.Add("X-Api-Key", apiKey);

        var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        using (response)
        {
            var body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            var (detail, code) = Problem(body);
            throw new SendwayException(
                response.StatusCode,
                detail ?? $"Sendway answered {(int)response.StatusCode} {response.ReasonPhrase}.",
                code,
                RetryAfter(response));
        }
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, JsonTypeInfo<T> type, CancellationToken cancellationToken)
    {
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            return await JsonSerializer.DeserializeAsync(stream, type, cancellationToken).ConfigureAwait(false)
                ?? throw new SendwayException(response.StatusCode, "Sendway answered with an empty body.", null, null);
        }
    }

    // The `detail` and `code` of an RFC 9457 problem; nulls when the body is something else.
    private static (string? Detail, string? Code) Problem(byte[] body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object
                ? (Text(root, "detail"), Text(root, "code"))
                : (null, null);
        }
        catch (JsonException)
        {
            return (null, null);
        }

        static string? Text(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static TimeSpan? RetryAfter(HttpResponseMessage response) =>
        response.Headers.RetryAfter switch
        {
            { Delta: { } delta } => delta,
            { Date: { } date } => date - DateTimeOffset.UtcNow is var wait && wait > TimeSpan.Zero ? wait : TimeSpan.Zero,
            _ => null
        };
}
