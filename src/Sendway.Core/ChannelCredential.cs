namespace Sendway.Core;

public sealed class ChannelCredential
{
    // Guid.Empty represents the shared/default credential (no tenant override).
    public required Guid TenantId { get; init; }

    public required string Channel { get; init; }

    public required string EncryptedPayload { get; set; }
}
