using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Sendway.Core.Tests;

/// <summary>
/// Gives each store test its own SQLite file, mirroring
/// Sendway.Service.Tests.TestIsolatedStorage's per-test-class isolation.
/// </summary>
internal sealed class SqliteTempDatabase : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "sendway-core-tests-" + Guid.NewGuid().ToString("N") + ".db");

    public IDbContextFactory<SendwayDbContext> Factory { get; }

    public SqliteTempDatabase()
    {
        var options = new DbContextOptionsBuilder<SendwayDbContext>()
            .UseSqlite($"Data Source={_path}")
            .Options;
        Factory = new PooledDbContextFactory<SendwayDbContext>(options);
    }

    public async Task EnsureCreatedAsync()
    {
        await using var db = await Factory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
    }

    public void Dispose()
    {
        try
        {
            File.Delete(_path);
        }
        catch (IOException)
        {
        }
    }
}
