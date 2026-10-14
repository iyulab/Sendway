using System.Collections.Concurrent;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace Sendway.Core;

public sealed class SmtpEmailSender : IEmailSender
{
    private readonly ICredentialStore _credentialStore;

    // One gate per sending account that has a connection limit, shared by every tenant that sends
    // through it — the provider counts connections per account, not per caller.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _accountGates = new(StringComparer.OrdinalIgnoreCase);

    public SmtpEmailSender(ICredentialStore credentialStore)
    {
        _credentialStore = credentialStore;
    }

    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        var options = await _credentialStore.GetAsync<SmtpOptions>(message.TenantId, ChannelCredentialNames.Smtp, cancellationToken)
            ?? throw new InvalidOperationException("Smtp channel credentials have not been configured.");

        var mimeMessage = new MimeMessage();
        mimeMessage.From.Add(MailboxAddress.Parse(options.FromAddress));
        AddRecipients(mimeMessage.To, message.To);
        AddRecipients(mimeMessage.Cc, message.Cc);
        AddRecipients(mimeMessage.Bcc, message.Bcc);
        mimeMessage.Subject = message.Subject;
        mimeMessage.Body = BuildBody(message.Body, message.HtmlBody);

        var (host, port) = SmtpProviderPresets.Resolve(options);

        // Waiting for a connection is part of the attempt: it ends with the attempt's own time limit.
        var gate = SmtpProviderPresets.MaxConnections(options) is { } limit
            ? _accountGates.GetOrAdd($"{host}:{port}:{options.Username ?? options.FromAddress}:{limit}", _ => new SemaphoreSlim(limit, limit))
            : null;

        if (gate is null)
        {
            await SendAsync(options, host, port, mimeMessage, cancellationToken);
            return;
        }

        await gate.WaitAsync(cancellationToken);
        try
        {
            await SendAsync(options, host, port, mimeMessage, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task SendAsync(SmtpOptions options, string host, int port, MimeMessage mimeMessage, CancellationToken cancellationToken)
    {
        // A fresh SmtpClient per send — one that failed to connect/authenticate isn't assumed
        // reusable when the message is attempted again.
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
    }

    // htmlBody absent → single plain-text part, byte-identical to pre-HTML-support wire format.
    // htmlBody present → multipart/alternative with plain-text kept as the mandatory fallback part,
    // per RFC 2046 (mail clients that can't render HTML fall back to it).
    internal static MimeEntity BuildBody(string body, string? htmlBody)
    {
        var builder = new BodyBuilder { TextBody = body };

        if (!string.IsNullOrEmpty(htmlBody))
        {
            builder.HtmlBody = htmlBody;
        }

        return builder.ToMessageBody();
    }

    private static void AddRecipients(InternetAddressList destination, IReadOnlyList<string> addresses)
    {
        foreach (var address in addresses)
        {
            destination.Add(EmailAddressValidator.Validate(address));
        }
    }
}
