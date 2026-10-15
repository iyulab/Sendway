using System.Security.Cryptography;
using System.Text;

namespace Sendway.Client.Tests;

public class WebhookTests
{
    private static readonly byte[] Key = Enumerable.Range(1, 24).Select(i => (byte)i).ToArray();
    private static readonly string Secret = "whsec_" + Convert.ToBase64String(Key);
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    private const string Body =
        """{"type":"message.sent","timestamp":"2026-10-15T12:00:00+00:00","data":{"id":"3f2b1c4e-0000-4000-8000-000000000001","channel":"email","recipient":"user@example.com","status":"Sent","error":null,"reason":null,"attempts":1,"acceptedAt":"2026-10-15T11:59:58+00:00","completedAt":"2026-10-15T12:00:00+00:00"}}""";

    private static string Sign(string id, long timestamp, string body, byte[]? key = null) =>
        "v1," + Convert.ToBase64String(HMACSHA256.HashData(key ?? Key, Encoding.UTF8.GetBytes($"{id}.{timestamp}.{body}")));

    private static SendwayWebhook Webhook() => new(Secret, timeProvider: new FixedTime(Now));

    [Fact]
    public void Verify_ASignedDelivery_ReadsTheEvent()
    {
        var timestamp = Now.ToUnixTimeSeconds();

        var received = Webhook().Verify("evt-1", timestamp.ToString(), Sign("evt-1", timestamp, Body), Body);

        Assert.Equal(MessageEvent.Sent, received.TypeText);
        Assert.Equal(MessageState.Sent, received.Data.Status);
        Assert.Null(received.Data.Reason);
    }

    [Fact]
    public void Verify_AcceptsAnyOfSeveralSignatures()
    {
        var timestamp = Now.ToUnixTimeSeconds();
        var signatures = Sign("evt-1", timestamp, Body, new byte[24]) + " " + Sign("evt-1", timestamp, Body);

        Webhook().Verify("evt-1", timestamp.ToString(), signatures, Body);
    }

    [Fact]
    public void Verify_AChangedBody_IsRefused()
    {
        var timestamp = Now.ToUnixTimeSeconds();

        Assert.Throws<WebhookVerificationException>(() =>
            Webhook().Verify("evt-1", timestamp.ToString(), Sign("evt-1", timestamp, Body), Body.Replace("\"Sent\"", "\"Failed\"")));
    }

    [Fact]
    public void Verify_ADeliveryOutsideTheTolerance_IsRefused()
    {
        var timestamp = Now.AddMinutes(-6).ToUnixTimeSeconds();

        Assert.Throws<WebhookVerificationException>(() =>
            Webhook().Verify("evt-1", timestamp.ToString(), Sign("evt-1", timestamp, Body), Body));
    }

    [Theory]
    [InlineData("", "1", "v1,x")]
    [InlineData("evt-1", "not-a-time", "v1,x")]
    [InlineData("evt-1", "1", "v2,abc")]
    public void Verify_AMalformedDelivery_IsRefused(string id, string timestamp, string signature)
    {
        Assert.Throws<WebhookVerificationException>(() => Webhook().Verify(id, timestamp, signature, Body));
    }

    [Fact]
    public void Constructor_RejectsASecretThatIsNotBase64()
    {
        Assert.Throws<ArgumentException>(() => new SendwayWebhook("whsec_not base64!"));
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
