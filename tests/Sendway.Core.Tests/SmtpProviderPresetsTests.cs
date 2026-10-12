using Sendway.Core;
using Xunit;

namespace Sendway.Core.Tests;

public class SmtpProviderPresetsTests
{
    [Fact]
    public void Resolve_WithGmailProvider_ReturnsGmailHostAndPort()
    {
        var options = new SmtpOptions { Provider = SmtpProvider.Gmail, FromAddress = "sender@example.com" };

        var (host, port) = SmtpProviderPresets.Resolve(options);

        Assert.Equal("smtp.gmail.com", host);
        Assert.Equal(587, port);
    }

    [Fact]
    public void Resolve_WithOffice365Provider_ReturnsOffice365HostAndPort()
    {
        var options = new SmtpOptions { Provider = SmtpProvider.Office365, FromAddress = "sender@example.com" };

        var (host, port) = SmtpProviderPresets.Resolve(options);

        Assert.Equal("smtp.office365.com", host);
        Assert.Equal(587, port);
    }

    [Fact]
    public void Resolve_WithExplicitHostAndProvider_PrefersExplicitHost()
    {
        var options = new SmtpOptions
        {
            Provider = SmtpProvider.Gmail,
            Host = "smtp.custom.example.com",
            Port = 25,
            FromAddress = "sender@example.com"
        };

        var (host, port) = SmtpProviderPresets.Resolve(options);

        Assert.Equal("smtp.custom.example.com", host);
        Assert.Equal(25, port);
    }

    [Fact]
    public void Resolve_WithNeitherHostNorProvider_ThrowsInvalidOperationException()
    {
        var options = new SmtpOptions { FromAddress = "sender@example.com" };

        Assert.Throws<InvalidOperationException>(() => SmtpProviderPresets.Resolve(options));
    }

    [Theory]
    [InlineData("{\"Provider\":\"Office365\",\"FromAddress\":\"noreply@example.com\"}")]
    [InlineData("{\"Provider\":1,\"FromAddress\":\"noreply@example.com\"}")]
    public void Provider_IsReadFromJsonByNameOrByNumber(string json)
    {
        var options = System.Text.Json.JsonSerializer.Deserialize<SmtpOptions>(json);

        Assert.Equal(SmtpProvider.Office365, options!.Provider);
    }

    [Fact]
    public void Provider_IsWrittenToJsonByName()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new SmtpOptions { Provider = SmtpProvider.Gmail, FromAddress = "noreply@example.com" });

        Assert.Contains("\"Gmail\"", json);
    }
}
