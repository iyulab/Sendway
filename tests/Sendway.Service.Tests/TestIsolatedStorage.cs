using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace Sendway.Service.Tests;

/// <summary>
/// Gives each WebApplicationFactory-backed test class its own SQLite file and Data Protection
/// key directory, so parallel test classes don't race on Program.cs's shared default paths. The
/// running service targets PostgreSQL (Program.cs); "Sendway:DatabaseProvider" = "Sqlite" tells
/// it to use this isolated file instead, so tests don't require a real Postgres server.
/// </summary>
internal sealed class TestIsolatedStorage : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sendway-tests-" + Guid.NewGuid().ToString("N"));

    public void Apply(WebHostBuilderContext context, IConfigurationBuilder configuration)
    {
        Directory.CreateDirectory(_root);
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Sendway:DatabaseProvider"] = "Sqlite",
            ["ConnectionStrings:Sendway"] = $"Data Source={Path.Combine(_root, "sendway.db")}",
            ["Sendway:DataProtectionKeyPath"] = Path.Combine(_root, "dp-keys")
        });
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
