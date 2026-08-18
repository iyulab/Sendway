using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace Sendway.Service.Tests;

/// <summary>
/// Gives each WebApplicationFactory-backed test class its own SQLite file and Data Protection
/// key directory, so parallel test classes don't race on Program.cs's shared default paths.
/// </summary>
internal sealed class TestIsolatedStorage : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sendway-tests-" + Guid.NewGuid().ToString("N"));

    public void Apply(WebHostBuilderContext context, IConfigurationBuilder configuration)
    {
        Directory.CreateDirectory(_root);
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
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
