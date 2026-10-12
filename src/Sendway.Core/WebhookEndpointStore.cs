using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Sendway.Core;

public interface IWebhookEndpointStore
{
    // Registers (or replaces) the tenant's endpoint and issues a new signing secret. The secret is
    // returned here and nowhere else.
    Task<WebhookEndpointRegistration> SetAsync(Guid tenantId, Uri url, CancellationToken cancellationToken = default);

    Task<WebhookEndpointInfo?> GetAsync(Guid tenantId, CancellationToken cancellationToken = default);

    // The endpoint together with its signing secret, for making a delivery.
    Task<WebhookEndpointRegistration?> GetForDeliveryAsync(Guid tenantId, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(Guid tenantId, CancellationToken cancellationToken = default);
}

public sealed record WebhookEndpointInfo(Uri Url, DateTimeOffset CreatedAt);

public sealed record WebhookEndpointRegistration(Uri Url, string Secret, DateTimeOffset CreatedAt);

public sealed class WebhookEndpointStore : IWebhookEndpointStore
{
    private const string ProtectorPurpose = "Sendway.WebhookSecret.v1";

    // 32 random bytes: inside the 24–64 byte range the Standard Webhooks specification asks for.
    private const int SecretByteLength = 32;

    private readonly IDbContextFactory<SendwayDbContext> _dbContextFactory;
    private readonly IDataProtector _protector;
    private readonly TimeProvider _timeProvider;

    public WebhookEndpointStore(
        IDbContextFactory<SendwayDbContext> dbContextFactory,
        IDataProtectionProvider dataProtectionProvider,
        TimeProvider timeProvider)
    {
        _dbContextFactory = dbContextFactory;
        _protector = dataProtectionProvider.CreateProtector(ProtectorPurpose);
        _timeProvider = timeProvider;
    }

    public async Task<WebhookEndpointRegistration> SetAsync(Guid tenantId, Uri url, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var secret = WebhookSignature.SecretPrefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(SecretByteLength));
        var now = _timeProvider.GetUtcNow();

        var endpoint = await db.WebhookEndpoints.FirstOrDefaultAsync(e => e.TenantId == tenantId, cancellationToken);
        if (endpoint is null)
        {
            db.WebhookEndpoints.Add(new WebhookEndpoint
            {
                TenantId = tenantId,
                Url = url.AbsoluteUri,
                EncryptedSecret = _protector.Protect(secret),
                CreatedAt = now
            });
        }
        else
        {
            endpoint.Url = url.AbsoluteUri;
            endpoint.EncryptedSecret = _protector.Protect(secret);
            endpoint.CreatedAt = now;
        }

        await db.SaveChangesAsync(cancellationToken);

        return new WebhookEndpointRegistration(url, secret, now);
    }

    public async Task<WebhookEndpointInfo?> GetAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var endpoint = await db.WebhookEndpoints.AsNoTracking().FirstOrDefaultAsync(e => e.TenantId == tenantId, cancellationToken);

        return endpoint is null ? null : new WebhookEndpointInfo(new Uri(endpoint.Url), endpoint.CreatedAt);
    }

    public async Task<WebhookEndpointRegistration?> GetForDeliveryAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var endpoint = await db.WebhookEndpoints.AsNoTracking().FirstOrDefaultAsync(e => e.TenantId == tenantId, cancellationToken);

        return endpoint is null
            ? null
            : new WebhookEndpointRegistration(new Uri(endpoint.Url), _protector.Unprotect(endpoint.EncryptedSecret), endpoint.CreatedAt);
    }

    public async Task<bool> DeleteAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var deleted = await db.WebhookEndpoints.Where(e => e.TenantId == tenantId).ExecuteDeleteAsync(cancellationToken);
        return deleted > 0;
    }
}
