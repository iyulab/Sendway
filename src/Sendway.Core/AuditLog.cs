using Microsoft.EntityFrameworkCore;

namespace Sendway.Core;

public interface IAuditLog
{
    Task AppendAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default);

    Task<AuditEvent?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    // Newest first. `before` continues a previous page: pass the last event of that page.
    Task<IReadOnlyList<AuditEvent>> ListAsync(Guid? tenantId, AuditCursor? before, int limit, CancellationToken cancellationToken = default);
}

// A position in the log. Events are ordered by time and then by id, so two events recorded in the
// same instant still have a definite order and a page boundary between them loses neither.
public sealed record AuditCursor(DateTimeOffset OccurredAt, Guid Id);

public sealed class AuditLog : IAuditLog
{
    private readonly IDbContextFactory<SendwayDbContext> _dbContextFactory;

    public AuditLog(IDbContextFactory<SendwayDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory;
    }

    public async Task AppendAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        db.AuditEvents.Add(auditEvent);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<AuditEvent?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.AuditEvents.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, cancellationToken);
    }

    public async Task<IReadOnlyList<AuditEvent>> ListAsync(Guid? tenantId, AuditCursor? before, int limit, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var query = db.AuditEvents.AsNoTracking();
        if (tenantId is { } id)
        {
            query = query.Where(e => e.TenantId == id);
        }

        if (before is { } cursor)
        {
            query = query.Where(e => e.OccurredAt < cursor.OccurredAt ||
                (e.OccurredAt == cursor.OccurredAt && e.Id < cursor.Id));
        }

        return await query
            .OrderByDescending(e => e.OccurredAt)
            .ThenByDescending(e => e.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }
}
