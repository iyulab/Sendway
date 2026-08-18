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

    public async Task<T?> GetAsync<T>(string channel, CancellationToken cancellationToken = default) where T : class
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var record = await db.ChannelCredentials
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Channel == channel, cancellationToken);

        if (record is null)
        {
            return null;
        }

        var json = _protector.Unprotect(record.EncryptedPayload);
        return JsonSerializer.Deserialize<T>(json);
    }

    public async Task SetAsync<T>(string channel, T value, CancellationToken cancellationToken = default) where T : class
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var json = JsonSerializer.Serialize(value);
        var encrypted = _protector.Protect(json);

        var record = await db.ChannelCredentials
            .FirstOrDefaultAsync(c => c.Channel == channel, cancellationToken);

        if (record is null)
        {
            db.ChannelCredentials.Add(new ChannelCredential { Channel = channel, EncryptedPayload = encrypted });
        }
        else
        {
            record.EncryptedPayload = encrypted;
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
