using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Sendway.Core;

public sealed class EncryptedCredentialStore : ICredentialStore
{
    private const string ProtectorPurpose = "Sendway.ChannelCredentials.v1";

    private readonly IDbContextFactory<SendwayDbContext> _dbContextFactory;
    private readonly IDataProtector _protector;

    public EncryptedCredentialStore(IDbContextFactory<SendwayDbContext> dbContextFactory, IDataProtectionProvider dataProtectionProvider)
    {
        _dbContextFactory = dbContextFactory;
        _protector = dataProtectionProvider.CreateProtector(ProtectorPurpose);
    }

    public async Task<T?> GetAsync<T>(Guid? tenantId, string channel, CancellationToken cancellationToken = default) where T : class
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        if (tenantId is { } id)
        {
            var tenantRecord = await db.ChannelCredentials
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.TenantId == id && c.Channel == channel, cancellationToken);

            if (tenantRecord is not null)
            {
                return Deserialize<T>(tenantRecord.EncryptedPayload);
            }
        }

        var sharedRecord = await db.ChannelCredentials
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.TenantId == Guid.Empty && c.Channel == channel, cancellationToken);

        return sharedRecord is null ? null : Deserialize<T>(sharedRecord.EncryptedPayload);
    }

    public async Task SetAsync<T>(Guid? tenantId, string channel, T value, CancellationToken cancellationToken = default) where T : class
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var json = JsonSerializer.Serialize(value);
        var encrypted = _protector.Protect(json);
        var key = tenantId ?? Guid.Empty;

        var record = await db.ChannelCredentials
            .FirstOrDefaultAsync(c => c.TenantId == key && c.Channel == channel, cancellationToken);

        if (record is null)
        {
            db.ChannelCredentials.Add(new ChannelCredential { TenantId = key, Channel = channel, EncryptedPayload = encrypted });
        }
        else
        {
            record.EncryptedPayload = encrypted;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private T? Deserialize<T>(string encryptedPayload) where T : class
    {
        var json = _protector.Unprotect(encryptedPayload);
        return JsonSerializer.Deserialize<T>(json);
    }
}
