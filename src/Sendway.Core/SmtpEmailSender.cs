using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace Sendway.Core;

public sealed class SmtpEmailSender : IEmailSender
{
    private readonly ICredentialStore _credentialStore;

    public SmtpEmailSender(ICredentialStore credentialStore)
    {
        _credentialStore = credentialStore;
    }

    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        // MailboxAddress.TryParse alone accepts a bare local-part with no "@" (e.g. "plainaddress")
        // as a valid mailbox — too lenient for rejecting a malformed address, so the "@" is checked
        // explicitly on top of it.
        if (!MailboxAddress.TryParse(message.To, out var to) || !to.Address.Contains('@'))
        {
            throw new InvalidRecipientException($"'{message.To}' is not a valid email address.");
        }

        var options = await _credentialStore.GetAsync<SmtpOptions>(message.TenantId, ChannelCredentialNames.Smtp, cancellationToken)
            ?? throw new InvalidOperationException("Smtp channel credentials have not been configured.");

        var mimeMessage = new MimeMessage();
        mimeMessage.From.Add(MailboxAddress.Parse(options.FromAddress));
        mimeMessage.To.Add(to);
        mimeMessage.Subject = message.Subject;
        mimeMessage.Body = new TextPart("plain") { Text = message.Body };

        var (host, port) = SmtpProviderPresets.Resolve(options);

        // A fresh SmtpClient per attempt — one that failed to connect/authenticate isn't assumed
        // reusable on retry.
        await RetryPolicy.ExecuteAsync(async () =>
        {
            using var client = new SmtpClient();
            await client.ConnectAsync(host, port, SecureSocketOptions.Auto, cancellationToken);

            if (!string.IsNullOrEmpty(options.Username))
            {
                if (string.IsNullOrEmpty(options.Password))
                {
                    throw new InvalidOperationException("SmtpOptions.Password is required when Username is set.");
                }

                await client.AuthenticateAsync(options.Username, options.Password, cancellationToken);
            }

            await client.SendAsync(mimeMessage, cancellationToken);
            await client.DisconnectAsync(true, cancellationToken);
        }, cancellationToken: cancellationToken);
    }
}
