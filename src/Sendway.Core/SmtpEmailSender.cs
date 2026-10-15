using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace Sendway.Core;

public sealed class SmtpEmailSender : IEmailSender, IDisposable
{
    private readonly ICredentialStore _credentialStore;
    private readonly SmtpConnectionPool _pool;

    // One gate per sending account that has a connection limit, shared by every tenant that sends
    // through it — the provider counts connections per account, not per caller.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _accountGates = new(StringComparer.OrdinalIgnoreCase);

    public SmtpEmailSender(ICredentialStore credentialStore, TimeProvider? timeProvider = null)
    {
        _credentialStore = credentialStore;
        _pool = new SmtpConnectionPool(timeProvider ?? TimeProvider.System);
    }

    public async Task<EmailDelivery> SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
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

        var account = $"{host}:{port}:{options.Username ?? options.FromAddress}";

        // A kept connection was authenticated with the password of its time; a changed password must
        // not keep using it.
        var session = $"{account}:{Fingerprint(options.Password)}";

        // Waiting for a connection is part of the attempt: it ends with the attempt's own time limit.
        // Kept connections count against the limit too, which holds because a new connection is opened
        // only when no kept one is free.
        var gate = SmtpProviderPresets.MaxConnections(options) is { } limit
            ? _accountGates.GetOrAdd($"{account}:{limit}", _ => new SemaphoreSlim(limit, limit))
            : null;

        try
        {
            if (gate is null)
            {
                return await DeliverAsync(session, options, host, port, mimeMessage, cancellationToken);
            }

            await gate.WaitAsync(cancellationToken);
            try
            {
                return await DeliverAsync(session, options, host, port, mimeMessage, cancellationToken);
            }
            finally
            {
                gate.Release();
            }
        }
        catch (SmtpCommandException ex) when (SmtpRefusals.AsFinal(ex) is { } final)
        {
            throw final;
        }
    }

    public void Dispose() => _pool.Dispose();

    private async Task<EmailDelivery> DeliverAsync(string session, SmtpOptions options, string host, int port, MimeMessage mimeMessage, CancellationToken cancellationToken)
    {
        if (_pool.TryTake(session) is { } kept)
        {
            try
            {
                kept.ForgetRefused();
                await kept.SendAsync(mimeMessage, cancellationToken);
                var delivery = new EmailDelivery(kept.TakeRefused());
                _pool.Return(session, kept);
                return delivery;
            }
            catch (Exception ex) when (IsClosedConnection(ex) && !cancellationToken.IsCancellationRequested)
            {
                // The server closed the kept connection since; that is not this message's failure.
                // It goes out on a new connection below, within the same attempt.
                kept.Dispose();
            }
            catch
            {
                kept.Dispose();
                throw;
            }
        }

        var client = new TrackingSmtpClient();
        try
        {
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
        }
        catch
        {
            // A connection that failed anywhere is never kept: it is not assumed usable again.
            client.Dispose();
            throw;
        }

        var refused = new EmailDelivery(client.TakeRefused());
        _pool.Return(session, client);
        return refused;
    }

    private static bool IsClosedConnection(Exception exception) => exception
        is ServiceNotConnectedException
        or IOException
        or SocketException
        or SmtpProtocolException
        or SmtpCommandException { StatusCode: SmtpStatusCode.ServiceNotAvailable };

    private static string Fingerprint(string? secret) =>
        secret is null ? "" : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)))[..16];

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
