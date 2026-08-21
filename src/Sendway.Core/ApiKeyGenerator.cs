using System.Security.Cryptography;
using System.Text;

namespace Sendway.Core;

public static class ApiKeyGenerator
{
    private const string Prefix = "sw_";

    public static (string PlaintextKey, string Hash) Generate()
    {
        var randomBytes = RandomNumberGenerator.GetBytes(32);
        var plaintextKey = Prefix + Convert.ToBase64String(randomBytes)
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
