using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Sendway.Core;

namespace Sendway.Service.Auth;

// Identifies the calling tenant from the X-Api-Key header. Runs in the authentication middleware,
// so a request without a usable key is refused before its body is read.
public sealed class TenantKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "TenantKey";

    private readonly ITenantStore _tenantStore;

    public TenantKeyAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        ITenantStore tenantStore)
        : base(options, logger, encoder)
    {
        _tenantStore = tenantStore;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-Api-Key", out var apiKeyValues) ||
            string.IsNullOrWhiteSpace(apiKeyValues))
        {
            return AuthenticateResult.NoResult();
        }

        var hash = ApiKeyGenerator.Hash(apiKeyValues.ToString());
        var tenant = await _tenantStore.GetByApiKeyHashAsync(hash, Context.RequestAborted);

        if (tenant is null || !tenant.Active)
        {
            return AuthenticateResult.Fail("The API key is unknown or belongs to a deactivated tenant.");
        }

        Context.SetTenant(tenant);

        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, tenant.Id.ToString())], Scheme.Name);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }
}
