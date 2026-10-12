using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Sendway.Core;

public sealed class SendwayDbContext(DbContextOptions<SendwayDbContext> options) : DbContext(options)
{
    public DbSet<ChannelCredential> ChannelCredentials => Set<ChannelCredential>();

    public DbSet<MessageRecord> MessageRecords => Set<MessageRecord>();

    public DbSet<Tenant> Tenants => Set<Tenant>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ChannelCredential>().HasKey(c => new { c.TenantId, c.Channel });

        modelBuilder.Entity<MessageRecord>(message =>
        {
            message.Property(m => m.Status).HasConversion<string>();
            message.Property(m => m.IdempotencyKey).HasMaxLength(255);
            message.Property(m => m.RequestHash).HasMaxLength(64);

            // What makes an idempotency key a reservation rather than a cache entry: a second
            // insert with the same tenant and key fails, even while the first message is still in
            // flight. Rows without a key are unconstrained (NULLs are distinct in a unique index).
            message.HasIndex(m => new { m.TenantId, m.IdempotencyKey }).IsUnique();

            // Serves the delivery worker's "next due message" lookup.
            message.HasIndex(m => new { m.Status, m.NextAttemptAt });
        });

        modelBuilder.Entity<Tenant>().HasIndex(t => t.ApiKeyHash).IsUnique();

        if (Database.IsSqlite())
        {
            UseSortableDateTimeOffsets(modelBuilder);
        }
    }

    // SQLite has no native date/time type, and EF Core refuses to compare or order DateTimeOffset
    // columns stored as text there. Storing them as their binary (long) form keeps the due-message
    // query translatable on SQLite; every other provider keeps its native type.
    private static void UseSortableDateTimeOffsets(ModelBuilder modelBuilder)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                if (property.ClrType == typeof(DateTimeOffset) || property.ClrType == typeof(DateTimeOffset?))
                {
                    property.SetValueConverter(new DateTimeOffsetToBinaryConverter());
                }
            }
        }
    }
}
