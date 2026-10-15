namespace Sendway.Core;

public interface IEmailSender
{
    // Returns once the provider has taken the message; what it refused for good among several
    // recipients while accepting the rest comes back in the result.
    Task<EmailDelivery> SendAsync(EmailMessage message, CancellationToken cancellationToken = default);
}
