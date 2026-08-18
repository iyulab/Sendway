namespace Sendway.Core;

public static class SmtpProviderPresets
{
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
                SmtpProvider.Office365 => ("smtp.office365.com", 587),
                _ => throw new ArgumentOutOfRangeException(nameof(options), provider, "Unknown SmtpProvider.")
            };
        }

        throw new InvalidOperationException("SmtpOptions.Host or SmtpOptions.Provider must be set.");
    }
}
