using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Sendway.Core;

namespace Sendway.Service.Auth;

// Checks the X-Admin-Key header against the configured administrator key. With no key configured,
// nobody is an administrator.
public sealed class AdminKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "AdminKey";

    private readonly IConfiguration _configuration;

    public AdminKeyAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IConfiguration configuration)
        : base(options, logger, encoder)
    {
        _configuration = configuration;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var expectedKey = _configuration["Sendway:AdminApiKey"];

        if (string.IsNullOrEmpty(expectedKey) ||
            !Request.Headers.TryGetValue("X-Admin-Key", out var providedKeyValues))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        if (!SecureEquals(providedKeyValues.ToString(), expectedKey))
        {
            return Task.FromResult(AuthenticateResult.Fail("The administrator key does not match."));
        }

        // There is one administrator key, so "who" is "whoever holds this key". Naming it by a
        // fingerprint still tells key generations apart once the key is replaced; the fingerprint
        // is a prefix of the key's hash and cannot be turned back into the key.
        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Role, "administrator"),
                new Claim(ClaimTypes.Name, "admin-key:" + ApiKeyGenerator.Hash(expectedKey)[..12].ToLowerInvariant())
            ],
            Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }

    // Compares the SHA-256 hashes of the two strings rather than the strings themselves: an
    // ordinary comparison returns early when lengths differ, while hashes are always the same
    // length, so nothing about the key's length leaks through timing.
    private static bool SecureEquals(string a, string b)
    {
        var aHash = SHA256.HashData(Encoding.UTF8.GetBytes(a));
        var bHash = SHA256.HashData(Encoding.UTF8.GetBytes(b));
        return CryptographicOperations.FixedTimeEquals(aHash, bHash);
    }
}
