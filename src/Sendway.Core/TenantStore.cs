using Microsoft.EntityFrameworkCore;

namespace Sendway.Core;

public sealed class TenantStore : ITenantStore
{
    private readonly IDbContextFactory<SendwayDbContext> _dbContextFactory;

    public TenantStore(IDbContextFactory<SendwayDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory;
    }

    public async Task<Tenant> CreateAsync(string name, string apiKeyHash, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = name,
            ApiKeyHash = apiKeyHash,
            Active = true,
            CreatedAt = DateTimeOffset.UtcNow
        };

        db.Tenants.Add(tenant);
        await db.SaveChangesAsync(cancellationToken);

        return tenant;
    }

    public async Task<Tenant?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        return await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, cancellationToken);
    }

    public async Task<Tenant?> GetByApiKeyHashAsync(string apiKeyHash, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        return await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.ApiKeyHash == apiKeyHash, cancellationToken);
    }

    public async Task<IReadOnlyList<Tenant>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        // SQLite's EF Core provider can't translate ORDER BY on DateTimeOffset server-side, so sort client-side.
        var tenants = await db.Tenants.AsNoTracking().ToListAsync(cancellationToken);
        return tenants.OrderBy(t => t.CreatedAt).ToList();
    }

    public async Task<bool> SetActiveAsync(Guid id, bool active, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (tenant is null)
        {
            return false;
        }

        tenant.Active = active;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> SetApiKeyHashAsync(Guid id, string apiKeyHash, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (tenant is null)
        {
            return false;
        }

        tenant.ApiKeyHash = apiKeyHash;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }
}
