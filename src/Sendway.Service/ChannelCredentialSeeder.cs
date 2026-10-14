using Microsoft.EntityFrameworkCore;
using Sendway.Core;

namespace Sendway.Service;

/// <summary>
/// Runs as a hosted service (not inline in Program.cs before app.Run()) because
/// WebApplicationFactory invokes the entry point in a discovery "probe" pass before test
/// overrides (ConfigureAppConfiguration/ConfigureServices) are applied. Code placed before
/// app.Run()/app.RunAsync() runs during that probe too, against the un-overridden config.
/// IHostedService.StartAsync only runs once the host is actually started, after overrides
/// are in effect.
/// </summary>
public sealed class ChannelCredentialSeeder : IHostedService
{
    private readonly IDbContextFactory<SendwayDbContext> _dbContextFactory;
    private readonly ICredentialStore _credentialStore;
    private readonly IConfiguration _configuration;

    public ChannelCredentialSeeder(
        IDbContextFactory<SendwayDbContext> dbContextFactory,
        ICredentialStore credentialStore,
        IConfiguration configuration)
    {
        _dbContextFactory = dbContextFactory;
        _credentialStore = credentialStore;
        _configuration = configuration;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using (var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            if (_configuration["Sendway:DatabaseProvider"] == "Sqlite")
            {
                await db.Database.EnsureCreatedAsync(cancellationToken);
            }
            else
            {
                await db.Database.MigrateAsync(cancellationToken);
            }
        }

        // A shared credential configuration supplies is owned by configuration: it replaces what an
        // earlier start or the admin API stored, so a changed value takes effect after a restart. A
        // channel configuration says nothing about keeps its stored value, managed through the API.
        foreach (var kind in ChannelCredentialKinds.All)
        {
            await kind.SeedAsync(_configuration, _credentialStore, cancellationToken);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
