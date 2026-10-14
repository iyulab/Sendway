namespace Sendway.Core;

public sealed class SmtpOptions
{
    public SmtpProvider? Provider { get; init; }
    public string? Host { get; init; }
    public int Port { get; init; } = 587;
    public string? Username { get; init; }
    public string? Password { get; init; }
    public required string FromAddress { get; init; }

    // How many connections may be open to this account at once. Unset, the provider's known limit
    // applies (SmtpProviderPresets.MaxConnections), or none of its own beyond the delivery concurrency.
    public int? MaxConnections { get; init; }
}
