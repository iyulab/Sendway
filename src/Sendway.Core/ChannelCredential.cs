namespace Sendway.Core;

public sealed class ChannelCredential
{
    public required string Channel { get; init; }

    public required string EncryptedPayload { get; set; }
}
