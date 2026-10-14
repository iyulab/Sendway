namespace Sendway.Core;

public static class SmtpProviderPresets
{
    // Exchange Online accepts at most three concurrent SMTP AUTH connections per mailbox and answers
    // a fourth with "432 4.3.2 Concurrent connections limit exceeded".
    private const string Office365Host = "smtp.office365.com";
    private const int Office365MaxConnections = 3;

    // The most connections to open to this account at once, or null for no limit of its own.
    public static int? MaxConnections(SmtpOptions options) =>
        options.MaxConnections ??
        (string.Equals(Resolve(options).Host, Office365Host, StringComparison.OrdinalIgnoreCase) ? Office365MaxConnections : null);

    public static (string Host, int Port) Resolve(SmtpOptions options)
    {
        if (!string.IsNullOrEmpty(options.Host))
        {
            return (options.Host, options.Port);
        }

        if (options.Provider is { } provider)
        {
            return provider switch
            {
                SmtpProvider.Gmail => ("smtp.gmail.com", 587),
                SmtpProvider.Office365 => (Office365Host, 587),
                _ => throw new ArgumentOutOfRangeException(nameof(options), provider, "Unknown SmtpProvider.")
            };
        }

        throw new InvalidOperationException("SmtpOptions.Host or SmtpOptions.Provider must be set.");
    }
}
