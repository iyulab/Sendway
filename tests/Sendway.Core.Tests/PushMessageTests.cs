// tests/Sendway.Core.Tests/PushMessageTests.cs
using Sendway.Core;
using Xunit;

namespace Sendway.Core.Tests;

public class PushMessageTests
{
    [Fact]
    public void Constructor_WithValidValues_SetsProperties()
    {
        var tenantId = Guid.NewGuid();

        var message = new PushMessage(tenantId, "device-token-123", "Title", "Body");

        Assert.Equal(tenantId, message.TenantId);
        Assert.Equal("device-token-123", message.DeviceToken);
        Assert.Equal("Title", message.Title);
        Assert.Equal("Body", message.Body);
    }

    [Theory]
    [InlineData("", "Title", "Body")]
    [InlineData("device-token-123", "", "Body")]
    [InlineData("device-token-123", "Title", "")]
    [InlineData(" ", "Title", "Body")]
    public void Constructor_WithBlankField_ThrowsArgumentException(string deviceToken, string title, string body)
    {
        Assert.Throws<ArgumentException>(() => new PushMessage(Guid.NewGuid(), deviceToken, title, body));
    }
}
