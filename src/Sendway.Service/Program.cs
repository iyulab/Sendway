using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.EntityFrameworkCore;
using Sendway.Core;
using Sendway.Service;
using Sendway.Service.Auth;
using Sendway.Service.Delivery;
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
builder.Services.AddSingleton<IMessageOutbox, MessageOutbox>();

builder.Services.AddSingleton<SmtpEmailSender>();
builder.Services.AddSingleton<GraphEmailSender>();
builder.Services.AddKeyedSingleton<IEmailSender>(ChannelCredentialNames.Smtp, (sp, _) => sp.GetRequiredService<SmtpEmailSender>());
builder.Services.AddKeyedSingleton<IEmailSender>(ChannelCredentialNames.EmailGraph, (sp, _) => sp.GetRequiredService<GraphEmailSender>());
builder.Services.AddSingleton<IEmailSender, EmailSenderRouter>();
builder.Services.AddSingleton<IPushSender, FcmPushSender>();
builder.Services.AddSingleton<ISmsSender>(sp => new SolapiSmsSender(
    new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
    {
        BaseAddress = SolapiSmsSender.DefaultBaseAddress
    },
    sp.GetRequiredService<ICredentialStore>(),
    sp.GetRequiredService<TimeProvider>()));

// Who is calling is settled by the authentication middleware, before an endpoint reads the request
// body: /messages by the tenant's API key, /admin by the administrator key.
builder.Services.AddAuthentication()
    .AddScheme<AuthenticationSchemeOptions, TenantKeyAuthenticationHandler>(TenantKeyAuthenticationHandler.SchemeName, null)
    .AddScheme<AuthenticationSchemeOptions, AdminKeyAuthenticationHandler>(AdminKeyAuthenticationHandler.SchemeName, null);
builder.Services.AddAuthorization();

builder.Services.AddRateLimiter(options =>
    options.AddPolicy<Guid, TenantRateLimitPolicy>(TenantRateLimitPolicy.PolicyName));

// Errors answer in one shape wherever they come from. An endpoint that refuses a request writes
// its own problem details (ApiProblem); the ones nobody wrote — an unreadable body, an unknown
// path, an unhandled failure — get theirs from the status code pages and the exception handler
// below, and ApiProblem.Complete gives all of them a `code`.
builder.Services.AddProblemDetails(options =>
    options.CustomizeProblemDetails = context => ApiProblem.Complete(context.ProblemDetails));

// An unreadable request body is answered as a 400 in every environment, rather than thrown where
// the environment is Development.
builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = false);

builder.Services.AddOptions<MessageDeliveryOptions>().BindConfiguration("Sendway:Delivery");
builder.Services.AddSingleton<MessageDeliverySignal>();
builder.Services.AddSingleton<MessageDeliveryProcessor>();
builder.Services.AddSingleton<IDeliveryProcessor>(sp => sp.GetRequiredService<MessageDeliveryProcessor>());

builder.Services.AddOptions<WebhookDeliveryOptions>().BindConfiguration("Sendway:Webhooks");
builder.Services.AddSingleton<IWebhookEndpointStore, WebhookEndpointStore>();
builder.Services.AddSingleton<WebhookOutbox>();
builder.Services.AddSingleton<WebhookDeliverySignal>();
builder.Services.AddSingleton<IWebhookSender>(sp => new HttpWebhookSender(
    // Redirects are not followed: the signed request goes to the registered URL or nowhere.
    new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) }),
    sp.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton<WebhookDeliveryProcessor>();
builder.Services.AddSingleton<IDeliveryProcessor>(sp => sp.GetRequiredService<WebhookDeliveryProcessor>());

// Order matters: the seeder migrates the schema, and the delivery worker must not claim before that.
builder.Services.AddHostedService<ChannelCredentialSeeder>();
builder.Services.AddHostedService<DeliveryWorker>();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

var messages = app.MapGroup("/messages")
    .RequireAuthorization(policy => policy
        .AddAuthenticationSchemes(TenantKeyAuthenticationHandler.SchemeName)
        .RequireAuthenticatedUser())
    .RequireRateLimiting(TenantRateLimitPolicy.PolicyName);
messages.MapSendEmailEndpoint();
messages.MapSendPushEndpoint();
messages.MapSendSmsEndpoint();
messages.MapGetMessageStatusEndpoint();

var admin = app.MapGroup("/admin")
    .RequireAuthorization(policy => policy
        .AddAuthenticationSchemes(AdminKeyAuthenticationHandler.SchemeName)
        .RequireAuthenticatedUser());
admin.MapCreateTenantEndpoint();
admin.MapListTenantsEndpoint();
admin.MapSetTenantActiveEndpoint();
admin.MapRotateTenantKeyEndpoint();
admin.MapSetTenantCredentialEndpoint();
admin.MapDeleteTenantCredentialEndpoint();
admin.MapTenantWebhookEndpoints();
admin.MapGetQueueStatsEndpoint();

app.Run();

public partial class Program { }
