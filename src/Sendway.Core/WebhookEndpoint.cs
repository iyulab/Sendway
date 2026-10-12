namespace Sendway.Core;

// Where a tenant wants to be told about its messages' outcomes. One per tenant.
public sealed class WebhookEndpoint
{
    public required Guid TenantId { get; init; }

    public required string Url { get; set; }

    // The signing secret, protected at rest. Unlike an API key it can't be stored as a hash: the
    // service needs the secret itself to sign each delivery.
    public required string EncryptedSecret { get; set; }

    public required DateTimeOffset CreatedAt { get; set; }
}
