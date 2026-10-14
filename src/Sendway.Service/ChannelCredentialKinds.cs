using System.Text.Json;
using Sendway.Core;

namespace Sendway.Service;

// Every channel credential the service knows: its shape and check, where its shared value may come
// from in configuration, and which cached client has to be rebuilt once it changes. The admin
// endpoints (per tenant and shared) and the seeder all work from this one table.
public abstract class ChannelCredentialKind
{
    protected ChannelCredentialKind(string name, string? configurationSection)
    {
        Name = name;
        ConfigurationSection = configurationSection;
    }

    public string Name { get; }

    // The configuration section that may supply the shared value, or null when the channel has no
    // shared value at all.
    public string? ConfigurationSection { get; }

    public bool Shareable => ConfigurationSection is not null;

    // While configuration supplies the shared value, configuration owns it: it is written again on
    // every start, so a value set through the API would not survive a restart.
    public bool IsConfigured(IConfiguration configuration) =>
        ConfigurationSection is { } section && configuration.GetSection(section).Exists();

    // Reads the credential from the request body, checks it and stores it. Returns what is wrong
    // with it, or null once it is stored.
    public abstract Task<string?> SetAsync(HttpRequest request, Guid? tenantId, CancellationToken cancellationToken);

    public abstract Task DeleteAsync(HttpContext httpContext, Guid? tenantId, CancellationToken cancellationToken);

    public abstract Task<bool> HasSharedValueAsync(ICredentialStore store, CancellationToken cancellationToken);

    public abstract Task SeedAsync(IConfiguration configuration, ICredentialStore store, CancellationToken cancellationToken);
}

public static class ChannelCredentialKinds
{
    public static readonly IReadOnlyList<ChannelCredentialKind> All =
    [
        new ChannelCredentialKind<SmtpOptions>(ChannelCredentialNames.Smtp, "Smtp", ChannelCredentialValidator.Validate),

        // No shared value: a tenant's email goes through Graph when a Graph credential resolves for
        // it, so a shared one would send every tenant's email through Graph, overrides or not.
        new ChannelCredentialKind<GraphOptions>(ChannelCredentialNames.EmailGraph, null, ChannelCredentialValidator.Validate,
            // GraphEmailSender caches one client per tenant, built from that tenant's credential.
            changed: (services, tenantId) => services.GetRequiredService<GraphEmailSender>().InvalidateTenant(tenantId!.Value)),

        new ChannelCredentialKind<FcmOptions>(ChannelCredentialNames.Fcm, "Fcm", ChannelCredentialValidator.Validate,
            // FcmPushSender caches one FirebaseApp per tenant; a tenant without an override built its
            // app from the shared credential, so a shared change rebuilds all of them.
            changed: (services, tenantId) =>
            {
                if (services.GetRequiredService<IPushSender>() is FcmPushSender fcm)
                {
                    if (tenantId is { } id)
                    {
                        fcm.InvalidateTenant(id);
                    }
                    else
                    {
                        fcm.InvalidateAll();
                    }
                }
            }),

        // SolapiSmsSender reads the credential on every send, so there is no cache to clear.
        new ChannelCredentialKind<SolapiOptions>(ChannelCredentialNames.Solapi, "Solapi", ChannelCredentialValidator.Validate)
    ];

    public static ChannelCredentialKind? Find(string name) => All.FirstOrDefault(kind => kind.Name == name);
}

internal sealed class ChannelCredentialKind<T> : ChannelCredentialKind where T : class
{
    private readonly Func<T, string?> _validate;
    private readonly Action<IServiceProvider, Guid?>? _changed;

    public ChannelCredentialKind(string name, string? configurationSection, Func<T, string?> validate, Action<IServiceProvider, Guid?>? changed = null)
        : base(name, configurationSection)
    {
        _validate = validate;
        _changed = changed;
    }

    public override async Task<string?> SetAsync(HttpRequest request, Guid? tenantId, CancellationToken cancellationToken)
    {
        // A body that isn't JSON, or lacks a required property, is the caller's mistake and is
        // answered as one — left to the framework it would surface as a 500.
        T? options;
        try
        {
            options = await request.ReadFromJsonAsync<T>(cancellationToken);
        }
        catch (JsonException ex)
        {
            return $"The credential could not be read: {ex.Message}";
        }

        if (options is null)
        {
            return "A credential is required.";
        }

        if (_validate(options) is { } error)
        {
            return error;
        }

        var services = request.HttpContext.RequestServices;
        await services.GetRequiredService<ICredentialStore>().SetAsync(tenantId, Name, options, cancellationToken);
        _changed?.Invoke(services, tenantId);
        return null;
    }

    public override async Task DeleteAsync(HttpContext httpContext, Guid? tenantId, CancellationToken cancellationToken)
    {
        var services = httpContext.RequestServices;
        await services.GetRequiredService<ICredentialStore>().DeleteAsync(tenantId, Name, cancellationToken);
        _changed?.Invoke(services, tenantId);
    }

    public override async Task<bool> HasSharedValueAsync(ICredentialStore store, CancellationToken cancellationToken) =>
        await store.GetAsync<T>(null, Name, cancellationToken) is not null;

    public override async Task SeedAsync(IConfiguration configuration, ICredentialStore store, CancellationToken cancellationToken)
    {
        if (IsConfigured(configuration) && configuration.GetSection(ConfigurationSection!).Get<T>() is { } options)
        {
            await store.SetAsync(null, Name, options, cancellationToken);
        }
    }
}
