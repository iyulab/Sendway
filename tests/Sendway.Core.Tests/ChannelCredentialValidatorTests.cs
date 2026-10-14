using Sendway.Core;
using Xunit;

namespace Sendway.Core.Tests;

public class ChannelCredentialValidatorTests
{
    private const string ServiceAccountJson =
        "{\"type\":\"service_account\",\"client_email\":\"sender@project.iam.gserviceaccount.com\",\"private_key\":\"-----BEGIN PRIVATE KEY-----\"}";

    [Fact]
    public void Smtp_WithHostAndFromAddress_IsAccepted()
    {
        Assert.Null(ChannelCredentialValidator.Validate(new SmtpOptions { Host = "smtp.example.com", FromAddress = "noreply@example.com" }));
    }

    [Fact]
    public void Smtp_WithProviderInsteadOfHost_IsAccepted()
    {
        var options = new SmtpOptions
        {
            Provider = SmtpProvider.Gmail,
            Username = "account@example.com",
            Password = "app-password",
            FromAddress = "account@example.com"
        };

        Assert.Null(ChannelCredentialValidator.Validate(options));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-email-address")]
    public void Smtp_WithUnusableFromAddress_IsRefused(string fromAddress)
    {
        var error = ChannelCredentialValidator.Validate(new SmtpOptions { Host = "smtp.example.com", FromAddress = fromAddress });

        Assert.Contains("fromAddress", error);
    }

    [Fact]
    public void Smtp_WithNeitherHostNorProvider_IsRefused()
    {
        var error = ChannelCredentialValidator.Validate(new SmtpOptions { FromAddress = "noreply@example.com" });

        Assert.Contains("host or provider", error);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(70000)]
    public void Smtp_WithPortOutOfRange_IsRefused(int port)
    {
        var error = ChannelCredentialValidator.Validate(new SmtpOptions { Host = "smtp.example.com", Port = port, FromAddress = "noreply@example.com" });

        Assert.Contains("port", error);
    }

    [Fact]
    public void Smtp_WithUsernameButNoPassword_IsRefused()
    {
        var error = ChannelCredentialValidator.Validate(
            new SmtpOptions { Host = "smtp.example.com", Username = "account", FromAddress = "noreply@example.com" });

        Assert.Contains("password", error);
    }

    [Fact]
    public void Graph_WithEverything_IsAccepted()
    {
        Assert.Null(ChannelCredentialValidator.Validate(Graph()));
    }

    [Theory]
    [InlineData("", "client", "secret", "noreply@example.com", "directoryId")]
    [InlineData("directory", " ", "secret", "noreply@example.com", "clientId")]
    [InlineData("directory", "client", "", "noreply@example.com", "clientSecret")]
    [InlineData("directory", "client", "secret", "not-an-email-address", "fromAddress")]
    public void Graph_WithAMissingPart_IsRefused(string directoryId, string clientId, string clientSecret, string fromAddress, string expectedInError)
    {
        var error = ChannelCredentialValidator.Validate(Graph(directoryId, clientId, clientSecret, fromAddress));

        Assert.Contains(expectedInError, error);
    }

    [Fact]
    public void Fcm_WithAServiceAccountKey_IsAccepted()
    {
        Assert.Null(ChannelCredentialValidator.Validate(new FcmOptions { CredentialsJson = ServiceAccountJson }));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"type\":\"service_account\"}")]
    [InlineData("{\"client_email\":\"sender@project.iam.gserviceaccount.com\",\"private_key\":\"\"}")]
    public void Fcm_WithoutAUsableServiceAccountKey_IsRefused(string credentialsJson)
    {
        Assert.NotNull(ChannelCredentialValidator.Validate(new FcmOptions { CredentialsJson = credentialsJson }));
    }

    [Fact]
    public void Solapi_WithEverything_IsAccepted()
    {
        Assert.Null(ChannelCredentialValidator.Validate(new SolapiOptions { ApiKey = "key", ApiSecret = "secret", From = "0212345678" }));
    }

    [Theory]
    [InlineData("", "secret", "0212345678", "apiKey")]
    [InlineData("key", "", "0212345678", "apiSecret")]
    [InlineData("key", "secret", "", "from")]
    [InlineData("key", "secret", "02-1234-5678", "from")]
    [InlineData("key", "secret", "+82212345678", "from")]
    public void Solapi_WithAMissingOrUnusablePart_IsRefused(string apiKey, string apiSecret, string from, string expectedInError)
    {
        var error = ChannelCredentialValidator.Validate(new SolapiOptions { ApiKey = apiKey, ApiSecret = apiSecret, From = from });

        Assert.NotNull(error);
        Assert.Contains(expectedInError, error);
    }

    private static GraphOptions Graph(
        string directoryId = "directory",
        string clientId = "client",
        string clientSecret = "secret",
        string fromAddress = "noreply@example.com") =>
        new() { DirectoryId = directoryId, ClientId = clientId, ClientSecret = clientSecret, FromAddress = fromAddress };
}
