using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Sendway.Core;

// Delivers queued events to their tenant's webhook endpoint.
public sealed class WebhookDeliveryProcessor : DeliveryProcessor<ClaimedWebhook>
{
    private readonly IWebhookEndpointStore _endpoints;
    private readonly IWebhookSender _sender;
    private readonly IOptions<WebhookDeliveryOptions> _options;

    public WebhookDeliveryProcessor(
        WebhookOutbox outbox,
        IWebhookEndpointStore endpoints,
        IWebhookSender sender,
        IOptions<WebhookDeliveryOptions> options,
        WebhookDeliverySignal signal,
        ILogger<WebhookDeliveryProcessor> logger)
        : base(outbox, signal, logger)
    {
        _endpoints = endpoints;
        _sender = sender;
        _options = options;
    }

    public override string Name => "Webhook";

    protected override DeliveryOptions Options => _options.Value;

    protected override async Task SendAsync(ClaimedWebhook item, CancellationToken cancellationToken)
    {
        // Looked up per attempt rather than captured when the event was queued, so a corrected URL
        // or a rotated secret applies to events already waiting.
        var endpoint = await _endpoints.GetForDeliveryAsync(item.TenantId, cancellationToken)
            ?? throw new WebhookRejectedException("The webhook endpoint was removed.");

        await _sender.SendAsync(endpoint.Url, endpoint.Secret, item.Id, item.Payload, cancellationToken);
    }

    protected override bool IsRejection(Exception exception) => exception is WebhookRejectedException;
}
