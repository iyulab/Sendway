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
            await db.Database.EnsureCreatedAsync(cancellationToken);
        }

        var smtp = _configuration.GetSection("Smtp").Get<SmtpOptions>();
        if (smtp is not null && await _credentialStore.GetAsync<SmtpOptions>(ChannelCredentialNames.Smtp, cancellationToken) is null)
        {
            await _credentialStore.SetAsync(ChannelCredentialNames.Smtp, smtp, cancellationToken);
        }

        var fcm = _configuration.GetSection("Fcm").Get<FcmOptions>();
        if (fcm is not null && await _credentialStore.GetAsync<FcmOptions>(ChannelCredentialNames.Fcm, cancellationToken) is null)
        {
            await _credentialStore.SetAsync(ChannelCredentialNames.Fcm, fcm, cancellationToken);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
