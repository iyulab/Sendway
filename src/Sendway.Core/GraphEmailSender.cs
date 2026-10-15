using System.Collections.Concurrent;
using Azure.Identity;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Models.ODataErrors;
using Microsoft.Graph.Users.Item.SendMail;
using Microsoft.Kiota.Abstractions;

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

    public async Task<EmailDelivery> SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
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

        var graphMessage = BuildGraphMessage(message);

        try
        {
            await tenantClient.Client.Users[tenantClient.FromAddress].SendMail.PostAsync(
                new SendMailPostRequestBody { Message = graphMessage, SaveToSentItems = false },
                cancellationToken: cancellationToken);
        }
        catch (ApiException ex) when (AsFinal(ex) is { } final)
        {
            throw final;
        }

        // Graph takes or refuses the request as a whole: it never names recipients it left out.
        return EmailDelivery.ToEveryone;
    }

    // sendMail answers 202 once Exchange has taken the message; a recipient that turns out not to
    // exist comes back later as a non-delivery report, not here. What Graph refuses on the spot is the
    // request itself (400, 413 for size), and the same message would be refused again, so it ends the
    // message. 401/403/404 — a credential, a permission, the sending mailbox — are for an operator to
    // fix and stay retried, as are throttling (429) and server errors.
    internal static Exception? AsFinal(ApiException exception)
    {
        var code = (exception as ODataError)?.Error?.Code;
        var detail = (exception as ODataError)?.Error?.Message ?? exception.Message;

        return exception.ResponseStatusCode switch
        {
            400 when code == "ErrorInvalidRecipients" =>
                new InvalidRecipientException($"Microsoft Graph refused the recipients: {detail}", exception),
            400 or 413 =>
                new MessageRejectedException($"Microsoft Graph refused the message ({code ?? exception.ResponseStatusCode.ToString()}): {detail}", exception),
            _ => null
        };
    }

    // SetTenantCredentialEndpoint calls this after registering/replacing this tenant's Graph
    // credentials — without it, the cached client would keep using the old credentials until
    // restart. Mirrors FcmPushSender.InvalidateTenant.
    public void InvalidateTenant(Guid tenantId)
    {
        _clients.TryRemove(tenantId, out _);
    }

    // Cc/Bcc are only assigned when non-empty. Graph's OData layer rejects an explicit JSON null
    // for these collection properties ("cannot be null but it can have null values") — assigning a
    // C# null here (as the empty-Cc/Bcc case used to) makes Kiota's backing store serialize that
    // explicit null and the send fails. Leaving the property untouched keeps it out of the
    // serialized body entirely.
    internal static Message BuildGraphMessage(EmailMessage message)
    {
        var graphMessage = new Message
        {
            Subject = message.Subject,
            Body = BuildBody(message.Body, message.HtmlBody),
            ToRecipients = ToRecipients(message.To)
        };

        if (message.Cc.Count > 0)
        {
            graphMessage.CcRecipients = ToRecipients(message.Cc);
        }

        if (message.Bcc.Count > 0)
        {
            graphMessage.BccRecipients = ToRecipients(message.Bcc);
        }

        return graphMessage;
    }

    // Graph's sendMail body carries exactly one ContentType (Text or Html) — unlike SMTP's
    // multipart/alternative, there is no built-in plain-text fallback part alongside HTML.
    private static ItemBody BuildBody(string body, string? htmlBody)
    {
        return !string.IsNullOrEmpty(htmlBody)
            ? new ItemBody { ContentType = BodyType.Html, Content = htmlBody }
            : new ItemBody { ContentType = BodyType.Text, Content = body };
    }

    // Callers only pass non-empty lists (To is guaranteed non-empty by EmailMessage; Cc/Bcc are
    // only passed through when SendAsync has already checked Count > 0).
    private static List<Recipient> ToRecipients(IReadOnlyList<string> addresses)
    {
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
