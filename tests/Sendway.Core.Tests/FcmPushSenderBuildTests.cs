using Sendway.Core;
using Xunit;

namespace Sendway.Core.Tests;

public class FcmPushSenderBuildTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Build_WithoutADeadline_LeavesThePlatformsTheirDefaults()
    {
        var message = FcmPushSender.Build(new PushMessage(Guid.NewGuid(), "token", "Title", "Body"), Now);

        Assert.Null(message.Android);
        Assert.Null(message.Apns);
    }

    [Fact]
    public void Build_WithADeadline_TellsBothPlatformsToDropTheMessageThen()
    {
        var expiresAt = Now.AddMinutes(3);

        var message = FcmPushSender.Build(new PushMessage(Guid.NewGuid(), "token", "Title", "Body") { ExpiresAt = expiresAt }, Now);

        Assert.Equal(TimeSpan.FromMinutes(3), message.Android!.TimeToLive);
        Assert.Equal(expiresAt.ToUnixTimeSeconds().ToString(), message.Apns!.Headers["apns-expiration"]);
    }

    [Fact]
    public void Build_WithADeadlineBeyondWhatFcmKeeps_CapsTheTimeToLive()
    {
        var message = FcmPushSender.Build(
            new PushMessage(Guid.NewGuid(), "token", "Title", "Body") { ExpiresAt = Now.AddDays(60) }, Now);

        Assert.Equal(TimeSpan.FromDays(28), message.Android!.TimeToLive);
    }
}
