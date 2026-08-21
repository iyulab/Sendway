using Sendway.Core;
using Xunit;

namespace Sendway.Core.Tests;

public class ApiKeyGeneratorTests
{
    [Fact]
    public void Generate_ReturnsKeyWithSwPrefix()
    {
        var (plaintextKey, _) = ApiKeyGenerator.Generate();

        Assert.StartsWith("sw_", plaintextKey);
    }

    [Fact]
    public void Generate_ReturnsDifferentKeysEachCall()
    {
        var (first, _) = ApiKeyGenerator.Generate();
        var (second, _) = ApiKeyGenerator.Generate();

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Hash_SameKey_ReturnsSameHash()
    {
        var (plaintextKey, _) = ApiKeyGenerator.Generate();

        Assert.Equal(ApiKeyGenerator.Hash(plaintextKey), ApiKeyGenerator.Hash(plaintextKey));
    }

    [Fact]
    public void Hash_DifferentKeys_ReturnDifferentHashes()
    {
        var (first, _) = ApiKeyGenerator.Generate();
        var (second, _) = ApiKeyGenerator.Generate();

        Assert.NotEqual(ApiKeyGenerator.Hash(first), ApiKeyGenerator.Hash(second));
    }

    [Fact]
    public void Generate_HashMatchesGeneratedPlaintextKey()
    {
        var (plaintextKey, hash) = ApiKeyGenerator.Generate();

        Assert.Equal(ApiKeyGenerator.Hash(plaintextKey), hash);
    }
}
