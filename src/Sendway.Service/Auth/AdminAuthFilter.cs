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

    // 두 문자열을 SHA-256으로 고정 길이 해시한 뒤 비교한다 — 원문 길이가 다르면 비교 전에 바로
    // 끝나버리는 일반적인 문자열 비교와 달리, 해시 길이가 항상 같아 길이 기반 타이밍 신호가 없다.
    private static bool SecureEquals(string a, string b)
    {
        var aHash = SHA256.HashData(Encoding.UTF8.GetBytes(a));
        var bHash = SHA256.HashData(Encoding.UTF8.GetBytes(b));
        return CryptographicOperations.FixedTimeEquals(aHash, bHash);
    }
}
