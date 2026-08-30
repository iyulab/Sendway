using Microsoft.Extensions.DependencyInjection;

namespace Sendway.Core;

// The single IEmailSender registered with DI. A tenant configuring "email-graph" credentials
// (PUT /admin/tenants/{id}/credentials/email-graph) sends through Graph instead of the SMTP
// default — everyone else is unaffected, matching the existing shared-default-with-per-tenant
// -override model ICredentialStore already provides for smtp/fcm.
//
// Depends on IEmailSender (via keyed DI), not the concrete Smtp/Graph sender types, so the
// dispatch decision is unit-testable with fakes instead of requiring a real SMTP/Graph endpoint.
public sealed class EmailSenderRouter : IEmailSender
{
    private readonly ICredentialStore _credentialStore;
    private readonly IEmailSender _smtpSender;
    private readonly IEmailSender _graphSender;

    public EmailSenderRouter(
        ICredentialStore credentialStore,
        [FromKeyedServices(ChannelCredentialNames.Smtp)] IEmailSender smtpSender,
        [FromKeyedServices(ChannelCredentialNames.EmailGraph)] IEmailSender graphSender)
    {
        _credentialStore = credentialStore;
        _smtpSender = smtpSender;
        _graphSender = graphSender;
    }

    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        var graphOptions = await _credentialStore.GetAsync<GraphOptions>(
            message.TenantId, ChannelCredentialNames.EmailGraph, cancellationToken);

        IEmailSender sender = graphOptions is not null ? _graphSender : _smtpSender;
        await sender.SendAsync(message, cancellationToken);
    }
}
