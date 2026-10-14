using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Sendway.Core;

namespace Sendway.Service.Auth;

// Identifies the calling tenant from the X-Api-Key header. Runs in the authentication middleware,
// so a request without a usable key is refused before its body is read.
//
// A tenant's previous key keeps working until its overlap ends. Requests made with it are let
// through but answered with an Api-Key-Expires header and logged, so whoever runs the calling
// service sees that it has not switched yet.
public sealed class TenantKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "TenantKey";

    public const string KeyExpiresHeader = "Api-Key-Expires";

    private readonly ITenantStore _tenantStore;
    private readonly TimeProvider _timeProvider;

    public TenantKeyAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        ITenantStore tenantStore,
        TimeProvider timeProvider)
        : base(options, logger, encoder)
    {
        _tenantStore = tenantStore;
        _timeProvider = timeProvider;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-Api-Key", out var apiKeyValues) ||
            string.IsNullOrWhiteSpace(apiKeyValues))
        {
            return AuthenticateResult.NoResult();
        }

        var hash = ApiKeyGenerator.Hash(apiKeyValues.ToString());
        var match = await _tenantStore.FindByApiKeyHashAsync(hash, _timeProvider.GetUtcNow(), Context.RequestAborted);

        if (match is not { Tenant.Active: true })
        {
            return AuthenticateResult.Fail("The API key is unknown or belongs to a deactivated tenant.");
        }

        var tenant = match.Tenant;
        if (match.ExpiresAt is { } expiresAt)
        {
            Response.Headers[KeyExpiresHeader] = expiresAt.ToString("R");
            Logger.LogWarning("Tenant {TenantId} called with its previous API key, which stops working at {ExpiresAt:O}", tenant.Id, expiresAt);
        }

        Context.SetTenant(tenant);

        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, tenant.Id.ToString())], Scheme.Name);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }
}
