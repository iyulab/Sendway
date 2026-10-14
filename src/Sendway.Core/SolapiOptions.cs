namespace Sendway.Core;

public sealed class SolapiOptions
{
    public required string ApiKey { get; init; }
    public required string ApiSecret { get; init; }

    // The sender number registered with the provider, digits only (e.g. "0212345678").
    public required string From { get; init; }
}
