using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Npgsql;

namespace Sendway.Core.Tests;

/// <summary>
/// Gives each store test class its own database. By default that is a throwaway SQLite file, so the
/// suite runs with nothing installed. When SENDWAY_TEST_POSTGRES holds a connection string to a
/// PostgreSQL server, each class instead gets a freshly created database on that server, built by
/// applying the migrations — the same provider, schema path and SQL the running service uses.
/// </summary>
internal sealed class TestDatabase : IDisposable
{
    public const string PostgresVariable = "SENDWAY_TEST_POSTGRES";

    private readonly string? _sqlitePath;

    public IDbContextFactory<SendwayDbContext> Factory { get; }

    public TestDatabase()
    {
        var builder = new DbContextOptionsBuilder<SendwayDbContext>();
        var postgresServer = Environment.GetEnvironmentVariable(PostgresVariable);

        if (string.IsNullOrWhiteSpace(postgresServer))
        {
            _sqlitePath = Path.Combine(Path.GetTempPath(), "sendway-core-tests-" + Guid.NewGuid().ToString("N") + ".db");
            builder.UseSqlite($"Data Source={_sqlitePath}");
        }
        else
        {
            var connectionString = new NpgsqlConnectionStringBuilder(postgresServer)
            {
                Database = "sendway_test_" + Guid.NewGuid().ToString("N")
            };
            builder.UseNpgsql(connectionString.ConnectionString);
        }

        Factory = new PooledDbContextFactory<SendwayDbContext>(builder.Options);
    }

    public async Task CreateAsync()
    {
        await using var db = await Factory.CreateDbContextAsync();

        if (_sqlitePath is null)
        {
            // Creates the database too, then brings it to the current schema one migration at a time.
            await db.Database.MigrateAsync();
        }
        else
        {
            // The migrations are generated for PostgreSQL; SQLite gets the schema straight from the model.
            await db.Database.EnsureCreatedAsync();
        }
    }

    public void Dispose()
    {
        try
        {
            if (_sqlitePath is null)
            {
                using var db = Factory.CreateDbContext();
                db.Database.EnsureDeleted();
            }
            else
            {
                File.Delete(_sqlitePath);
            }
        }
        catch (Exception ex) when (ex is IOException or NpgsqlException)
        {
        }
    }
}
