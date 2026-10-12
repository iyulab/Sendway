using System.Security.Cryptography;
using System.Text;

namespace Sendway.Service.Auth;

public sealed class AdminAuthFilter : IEndpointFilter
{
    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var httpContext = context.HttpContext;
        var configuration = httpContext.RequestServices.GetRequiredService<IConfiguration>();
        var expectedKey = configuration["Sendway:AdminApiKey"];

        if (string.IsNullOrEmpty(expectedKey) ||
            !httpContext.Request.Headers.TryGetValue("X-Admin-Key", out var providedKeyValues) ||
            !SecureEquals(providedKeyValues.ToString(), expectedKey))
        {
            return ValueTask.FromResult<object?>(Results.Unauthorized());
        }

        return next(context);
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
