using System.Net;
using System.Text;

namespace Sendway.Core;

public interface IWebhookSender
{
    // Makes one delivery attempt. Completes when the endpoint acknowledged with a 2xx status;
    // throws WebhookRejectedException when the endpoint declined further deliveries, and any other
    // exception when the attempt failed and may be retried.
    Task SendAsync(Uri url, string secret, Guid eventId, string body, CancellationToken cancellationToken = default);
}

// The receiving side has said it will not take this event, so it is not retried.
public sealed class WebhookRejectedException : Exception
{
    public WebhookRejectedException(string message) : base(message)
    {
    }
}

public sealed class HttpWebhookSender : IWebhookSender
{
    private readonly HttpClient _httpClient;
    private readonly TimeProvider _timeProvider;

    public HttpWebhookSender(HttpClient httpClient, TimeProvider timeProvider)
    {
        _httpClient = httpClient;
        _timeProvider = timeProvider;
    }

    public async Task SendAsync(Uri url, string secret, Guid eventId, string body, CancellationToken cancellationToken = default)
    {
        var id = eventId.ToString();

        // The timestamp is that of this attempt, not of the event: receivers reject signatures that
        // are too old, and a retry hours later must still verify.
        var timestamp = _timeProvider.GetUtcNow().ToUnixTimeSeconds();

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Add(WebhookSignature.IdHeader, id);
        request.Headers.Add(WebhookSignature.TimestampHeader, timestamp.ToString());
        request.Headers.Add(WebhookSignature.SignatureHeader, WebhookSignature.Compute(secret, id, timestamp, body));

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            return;
        }

        // 410 Gone is how a Standard Webhooks receiver says it no longer wants these events.
        if (response.StatusCode == HttpStatusCode.Gone)
        {
            throw new WebhookRejectedException("The endpoint responded 410 Gone.");
        }

        throw new HttpRequestException($"The endpoint responded {(int)response.StatusCode}.");
    }
}
