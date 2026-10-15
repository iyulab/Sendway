using System.Security.Cryptography;
using System.Text;

namespace Sendway.Core;

public static class ApiKeyGenerator
{
    private const string Prefix = "sw_";

    // A sandbox tenant's key says so, so one that ends up in a production configuration stands out.
    private const string SandboxPrefix = "sw_test_";

    public static (string PlaintextKey, string Hash) Generate(bool sandbox = false)
    {
        var randomBytes = RandomNumberGenerator.GetBytes(32);
        var plaintextKey = (sandbox ? SandboxPrefix : Prefix) + Convert.ToBase64String(randomBytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        return (plaintextKey, Hash(plaintextKey));
    }

    public static string Hash(string plaintextKey)
    {
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(plaintextKey));
        return Convert.ToHexString(hashBytes);
    }
}
