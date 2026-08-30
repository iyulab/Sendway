using System.Collections.Concurrent;
using Azure.Identity;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Users.Item.SendMail;

namespace Sendway.Core;

public sealed class GraphEmailSender : IEmailSender
{
    private readonly ICredentialStore _credentialStore;

    // Same rationale as FcmPushSender: token acquisition/refresh is handled internally by
    // ClientSecretCredential, but constructing the GraphServiceClient per tenant is still worth
    // caching rather than rebuilding on every send. Keyed per Sendway tenant (not to be confused
    // with GraphOptions.DirectoryId, Azure AD's own tenant concept).
    private readonly ConcurrentDictionary<Guid, Lazy<Task<TenantGraphClient>>> _clients = new();

    public GraphEmailSender(ICredentialStore credentialStore)
    {
        _credentialStore = credentialStore;
    }

    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        return RetryPolicy.ExecuteAsync(async () =>
        {
            var lazyClient = _clients.GetOrAdd(
                message.TenantId,
                tenantId => new Lazy<Task<TenantGraphClient>>(
                    () => CreateClientAsync(tenantId, _credentialStore),
                    LazyThreadSafetyMode.ExecutionAndPublication));

            TenantGraphClient tenantClient;
            try
            {
                tenantClient = await lazyClient.Value;
            }
            catch
            {
                // A faulted creation must not stay cached forever — remove it so the next send
                // retries instead of rethrowing the same stale failure until process restart.
                _clients.TryRemove(new KeyValuePair<Guid, Lazy<Task<TenantGraphClient>>>(message.TenantId, lazyClient));
                throw;
            }

            // Validated (and any InvalidRecipientException thrown) before the Graph call itself —
            // same "no partial send" guarantee SmtpEmailSender gives, and avoids having to guess at
            // Graph's own error-code taxonomy for malformed recipients without a live tenant to
            // verify it against.
            var graphMessage = new Message
            {
                Subject = message.Subject,
                Body = BuildBody(message.Body, message.HtmlBody),
                ToRecipients = ToRecipients(message.To),
                CcRecipients = ToRecipients(message.Cc),
                BccRecipients = ToRecipients(message.Bcc)
            };

            await tenantClient.Client.Users[tenantClient.FromAddress].SendMail.PostAsync(
                new SendMailPostRequestBody { Message = graphMessage, SaveToSentItems = false },
                cancellationToken: cancellationToken);
        }, cancellationToken: cancellationToken);
    }

    // SetTenantCredentialEndpoint calls this after registering/replacing this tenant's Graph
    // credentials — without it, the cached client would keep using the old credentials until
    // restart. Mirrors FcmPushSender.InvalidateTenant.
    public void InvalidateTenant(Guid tenantId)
    {
        _clients.TryRemove(tenantId, out _);
    }

    // Graph's sendMail body carries exactly one ContentType (Text or Html) — unlike SMTP's
    // multipart/alternative, there is no built-in plain-text fallback part alongside HTML.
    private static ItemBody BuildBody(string body, string? htmlBody)
    {
        return !string.IsNullOrEmpty(htmlBody)
            ? new ItemBody { ContentType = BodyType.Html, Content = htmlBody }
            : new ItemBody { ContentType = BodyType.Text, Content = body };
    }

    private static List<Recipient>? ToRecipients(IReadOnlyList<string> addresses)
    {
        if (addresses.Count == 0)
        {
            return null;
        }

        var recipients = new List<Recipient>(addresses.Count);
        foreach (var address in addresses)
        {
            var mailbox = EmailAddressValidator.Validate(address);
            recipients.Add(new Recipient { EmailAddress = new EmailAddress { Address = mailbox.Address } });
        }

        return recipients;
    }

    private static async Task<TenantGraphClient> CreateClientAsync(Guid tenantId, ICredentialStore credentialStore)
    {
        var options = await credentialStore.GetAsync<GraphOptions>(tenantId, ChannelCredentialNames.EmailGraph)
            ?? throw new InvalidOperationException("Graph channel credentials have not been configured.");

        var credential = new ClientSecretCredential(options.DirectoryId, options.ClientId, options.ClientSecret);
        var client = new GraphServiceClient(credential, ["https://graph.microsoft.com/.default"]);
        return new TenantGraphClient(client, options.FromAddress);
    }

    private sealed record TenantGraphClient(GraphServiceClient Client, string FromAddress);
}
