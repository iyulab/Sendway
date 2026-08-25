// tests/Sendway.Core.Tests/EmailMessageTests.cs
using Sendway.Core;
using Xunit;

namespace Sendway.Core.Tests;

public class EmailMessageTests
{
    [Fact]
    public void Constructor_WithValidValues_SetsProperties()
    {
        var tenantId = Guid.NewGuid();

        var message = new EmailMessage(tenantId, ["user@example.com"], "Subject", "Body");

        Assert.Equal(tenantId, message.TenantId);
        Assert.Equal(["user@example.com"], message.To);
        Assert.Empty(message.Cc);
        Assert.Empty(message.Bcc);
        Assert.Equal("Subject", message.Subject);
        Assert.Equal("Body", message.Body);
    }

    [Fact]
    public void Constructor_WithCcAndBcc_SetsProperties()
    {
        var message = new EmailMessage(
            Guid.NewGuid(),
            ["user@example.com"],
            "Subject",
            "Body",
            cc: ["cc@example.com"],
            bcc: ["bcc@example.com"]);

        Assert.Equal(["cc@example.com"], message.Cc);
        Assert.Equal(["bcc@example.com"], message.Bcc);
    }

    [Fact]
    public void Constructor_WithEmptyToList_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => new EmailMessage(Guid.NewGuid(), [], "Subject", "Body"));
    }

    [Fact]
    public void Constructor_WithBlankAddressInTo_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => new EmailMessage(Guid.NewGuid(), [" "], "Subject", "Body"));
    }

    [Theory]
    [InlineData("", "Body")]
    [InlineData("Subject", "")]
    public void Constructor_WithBlankField_ThrowsArgumentException(string subject, string body)
    {
        Assert.Throws<ArgumentException>(() => new EmailMessage(Guid.NewGuid(), ["user@example.com"], subject, body));
    }
}
