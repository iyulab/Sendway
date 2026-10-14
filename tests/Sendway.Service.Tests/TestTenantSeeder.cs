using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Sendway.Core;

namespace Sendway.Service.Tests;

internal static class TestTenantSeeder
{
    // A message is accepted only on a channel some credential lets the tenant send on, so by default
    // the tenant gets one of its own for every channel. Tests replace the senders with fakes, which never
    // read these values; a test about a tenant with nothing configured passes withChannelCredentials: false.
    public static async Task<(Guid TenantId, string ApiKey)> SeedAsync(
        WebApplicationFactory<Program> factory,
        string name = "test-tenant",
        bool withChannelCredentials = true)
    {
        var tenantStore = factory.Services.GetRequiredService<ITenantStore>();
        var (plaintextKey, hash) = ApiKeyGenerator.Generate();
        var tenant = await tenantStore.CreateAsync(name, hash);

        if (withChannelCredentials)
        {
            var credentials = factory.Services.GetRequiredService<ICredentialStore>();
            await credentials.SetAsync(tenant.Id, ChannelCredentialNames.Smtp,
                new SmtpOptions { Host = "smtp.test.invalid", FromAddress = "sender@example.com" });
            await credentials.SetAsync(tenant.Id, ChannelCredentialNames.Fcm,
                new FcmOptions { CredentialsJson = "{}" });
            await credentials.SetAsync(tenant.Id, ChannelCredentialNames.Solapi,
                new SolapiOptions { ApiKey = "test-key", ApiSecret = "test-secret", From = "0212345678" });
        }

        return (tenant.Id, plaintextKey);
    }
}
