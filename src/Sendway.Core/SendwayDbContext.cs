using Microsoft.EntityFrameworkCore;

namespace Sendway.Core;

public sealed class SendwayDbContext(DbContextOptions<SendwayDbContext> options) : DbContext(options)
{
    public DbSet<ChannelCredential> ChannelCredentials => Set<ChannelCredential>();

    public DbSet<MessageRecord> MessageRecords => Set<MessageRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ChannelCredential>().HasKey(c => c.Channel);

        modelBuilder.Entity<MessageRecord>().Property(m => m.Status).HasConversion<string>();
    }
}
