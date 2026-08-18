namespace Sendway.Core;

public sealed class SmtpOptions
{
    public required string Host { get; init; }
    public int Port { get; init; } = 587;
    public string? Username { get; init; }
    public string? Password { get; init; }
    public required string FromAddress { get; init; }
}
