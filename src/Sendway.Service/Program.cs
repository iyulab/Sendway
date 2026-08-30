using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.EntityFrameworkCore;
using Sendway.Core;
using Sendway.Service;
using Sendway.Service.Auth;
using Sendway.Service.Endpoints;
using Sendway.Service.Endpoints.Admin;

var builder = WebApplication.CreateBuilder(args);

// Configuration is resolved lazily (from IConfiguration/IServiceProvider inside each delegate,
// not into a local variable here) because WebApplicationFactory-based tests apply their
// per-test overrides (ConfigureAppConfiguration) after this point in the pipeline runs; a local
// `var` captured here would freeze in the un-overridden default and cause parallel test classes
// to collide on the same file (see tests/Sendway.Service.Tests/TestIsolatedStorage.cs).
builder.Services.AddDataProtection().SetApplicationName("Sendway");
builder.Services.AddOptions<KeyManagementOptions>()
    .Configure<IConfiguration, ILoggerFactory>((options, configuration, loggerFactory) =>
    {
        var keyPath = configuration["Sendway:DataProtectionKeyPath"] ?? "dp-keys";
        options.XmlRepository = new FileSystemXmlRepository(new DirectoryInfo(keyPath), loggerFactory);
    });

builder.Services.AddDbContextFactory<SendwayDbContext>((services, options) =>
{
    var configuration = services.GetRequiredService<IConfiguration>();
    var connectionString = configuration.GetConnectionString("Sendway")
        ?? "Host=localhost;Database=sendway;Username=postgres;Password=postgres";

    // Tests substitute an isolated Sqlite file per test class instead of standing up a real
    // Postgres server (see Sendway.Service.Tests.TestIsolatedStorage).
    if (configuration["Sendway:DatabaseProvider"] == "Sqlite")
    {
        options.UseSqlite(connectionString);
    }
    else
    {
        options.UseNpgsql(connectionString);
    }
});
builder.Services.AddSingleton<ICredentialStore, EncryptedCredentialStore>();
builder.Services.AddSingleton<ITenantStore, TenantStore>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IMessageStatusStore, MessageStatusStore>();

builder.Services.AddSingleton<SmtpEmailSender>();
builder.Services.AddSingleton<GraphEmailSender>();
builder.Services.AddKeyedSingleton<IEmailSender>(ChannelCredentialNames.Smtp, (sp, _) => sp.GetRequiredService<SmtpEmailSender>());
builder.Services.AddKeyedSingleton<IEmailSender>(ChannelCredentialNames.EmailGraph, (sp, _) => sp.GetRequiredService<GraphEmailSender>());
builder.Services.AddSingleton<IEmailSender, EmailSenderRouter>();
builder.Services.AddSingleton<IPushSender, FcmPushSender>();

builder.Services.AddMemoryCache();

builder.Services.AddHostedService<ChannelCredentialSeeder>();

var app = builder.Build();

var messages = app.MapGroup("/messages")
    .AddEndpointFilter<TenantAuthFilter>()
    .AddEndpointFilter<IdempotencyFilter>()
    .AddEndpointFilter<TenantRateLimitFilter>();
messages.MapSendEmailEndpoint();
messages.MapSendPushEndpoint();
messages.MapGetMessageStatusEndpoint();

var admin = app.MapGroup("/admin").AddEndpointFilter<AdminAuthFilter>();
admin.MapCreateTenantEndpoint();
admin.MapListTenantsEndpoint();
admin.MapSetTenantActiveEndpoint();
admin.MapRotateTenantKeyEndpoint();
admin.MapSetTenantCredentialEndpoint();

app.Run();

public partial class Program { }
