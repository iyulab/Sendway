// tests/Sendway.Core.Tests/EmailMessageTests.cs
using Sendway.Core;
using Xunit;

namespace Sendway.Core.Tests;

public class EmailMessageTests
{
    [Fact]
    public void Constructor_WithValidValues_SetsProperties()
    {
        var message = new EmailMessage("user@example.com", "Subject", "Body");

        Assert.Equal("user@example.com", message.To);
        Assert.Equal("Subject", message.Subject);
        Assert.Equal("Body", message.Body);
    }

    [Theory]
    [InlineData("", "Subject", "Body")]
    [InlineData("user@example.com", "", "Body")]
    [InlineData("user@example.com", "Subject", "")]
    [InlineData(" ", "Subject", "Body")]
    public void Constructor_WithBlankField_ThrowsArgumentException(string to, string subject, string body)
    {
        Assert.Throws<ArgumentException>(() => new EmailMessage(to, subject, body));
    }
}
