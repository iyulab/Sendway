using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Sendway.Core;
using Xunit;

namespace Sendway.Core.Tests;

public class SolapiSmsSenderTests
{
    private static readonly SolapiOptions Credential = new() { ApiKey = "test-key", ApiSecret = "test-secret", From = "0212345678" };

    private readonly ManualTimeProvider _time = new();
    private readonly RecordingHandler _handler = new();
    private readonly SolapiSmsSender _sender;

    public SolapiSmsSenderTests()
    {
        _sender = new SolapiSmsSender(
            new HttpClient(_handler) { BaseAddress = new Uri("https://solapi.test/") },
            new SingleCredentialStore(ChannelCredentialNames.Solapi, Credential),
            _time);
    }

    private static SmsMessage Sms(string to, string body = "Hello") => new(Guid.NewGuid(), to, body);

    [Fact]
    public async Task Send_ToAKoreanNumber_PostsItTheWayItIsDialedInKorea()
    {
        await _sender.SendAsync(Sms("+821012345678", "안녕하세요"));

        Assert.Equal(HttpMethod.Post, _handler.Request!.Method);
        Assert.Equal("https://solapi.test/messages/v4/send-many/detail", _handler.Request.RequestUri!.ToString());
        var message = _handler.Body!.RootElement.GetProperty("messages").EnumerateArray().Single();
        Assert.Equal("01012345678", message.GetProperty("to").GetString());
        Assert.Equal("0212345678", message.GetProperty("from").GetString());
        Assert.Equal("안녕하세요", message.GetProperty("text").GetString());
        Assert.Equal("82", message.GetProperty("country").GetString());
        Assert.True(message.GetProperty("autoTypeDetect").GetBoolean());
        Assert.False(message.TryGetProperty("type", out _));
    }

    [Fact]
    public async Task Send_ToANumberAbroad_PostsAnSmsWithTheCountryCode()
    {
        await _sender.SendAsync(Sms("+819012345678"));

        var message = _handler.Body!.RootElement.GetProperty("messages").EnumerateArray().Single();
        Assert.Equal("9012345678", message.GetProperty("to").GetString());
        Assert.Equal("81", message.GetProperty("country").GetString());
        Assert.Equal("SMS", message.GetProperty("type").GetString());
        Assert.False(message.TryGetProperty("autoTypeDetect", out _));
    }

    [Fact]
    public async Task Send_SignsTheRequestWithTheApiSecret()
    {
        await _sender.SendAsync(Sms("+821012345678"));

        var header = _handler.Request!.Headers.GetValues("Authorization").Single();
        var match = Regex.Match(header, "^HMAC-SHA256 apiKey=test-key, date=(?<date>[^,]+), salt=(?<salt>[0-9a-f]{32}), signature=(?<signature>[0-9a-f]{64})$");
        Assert.True(match.Success, header);
        Assert.Equal(_time.GetUtcNow().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"), match.Groups["date"].Value);

        var expected = Convert.ToHexStringLower(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes("test-secret"),
            Encoding.UTF8.GetBytes(match.Groups["date"].Value + match.Groups["salt"].Value)));
        Assert.Equal(expected, match.Groups["signature"].Value);
    }

    [Theory]
    [InlineData("1061", MessageFailureReasons.MessageRefused)]
    [InlineData("1029", MessageFailureReasons.MessageRefused)]
    [InlineData("2254", MessageFailureReasons.MessageRefused)]
    [InlineData("1065", MessageFailureReasons.RecipientRefused)]
    [InlineData("2065", MessageFailureReasons.RecipientRefused)]
    [InlineData("1069", MessageFailureReasons.RecipientRateLimited)]
    public async Task Send_WhenTheMessageItselfIsRefused_RejectsItWithTheReason(string statusCode, string reason)
    {
        _handler.Respond(HttpStatusCode.OK, Failed(statusCode, "refused"));

        var ex = await Assert.ThrowsAsync<MessageRejectedException>(() => _sender.SendAsync(Sms("+821012345678")));

        Assert.Equal($"Solapi refused the message: {statusCode} refused", ex.Message);
        Assert.Equal(reason, ex.Reason);
    }

    [Theory]
    [InlineData("1030")]
    [InlineData("1062")]
    [InlineData("2024")]
    public async Task Send_WhenRefusedForAReasonThatCanClearUp_FailsTheAttempt(string statusCode)
    {
        _handler.Respond(HttpStatusCode.OK, Failed(statusCode, "not now"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _sender.SendAsync(Sms("+821012345678")));

        Assert.Equal($"Solapi refused the message: {statusCode} not now", ex.Message);
    }

    [Fact]
    public async Task Send_WhenTheRequestIsRefused_FailsTheAttemptWithTheProviderError()
    {
        _handler.Respond(HttpStatusCode.Forbidden, """{"errorCode":"InvalidApiKey","errorMessage":"Invalid API Key"}""");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _sender.SendAsync(Sms("+821012345678")));

        Assert.Equal("Solapi answered 403: InvalidApiKey Invalid API Key", ex.Message);
    }

    [Fact]
    public async Task Send_WithoutACredential_FailsTheAttempt()
    {
        var sender = new SolapiSmsSender(new HttpClient(_handler), new SingleCredentialStore("other", Credential), _time);

        await Assert.ThrowsAsync<InvalidOperationException>(() => sender.SendAsync(Sms("+821012345678")));
        Assert.Null(_handler.Request);
    }

    [Theory]
    [InlineData("+15551234567")]
    [InlineData("+6591234567")]
    [InlineData("+8613812345678")]
    public void Check_ADestinationTheProviderDoesNotServe_IsRefused(string to)
    {
        Assert.NotNull(_sender.Check(Sms(to)));
    }

    [Fact]
    public void Check_ADomesticMessage_MayBeUpTo2000Bytes()
    {
        Assert.Null(_sender.Check(Sms("+821012345678", new string('가', 1000))));
        Assert.NotNull(_sender.Check(Sms("+821012345678", new string('가', 1000) + "a")));
    }

    [Fact]
    public void Check_AMessageAbroad_MayBeUpTo90Bytes()
    {
        Assert.Null(_sender.Check(Sms("+819012345678", new string('a', 90))));
        Assert.NotNull(_sender.Check(Sms("+819012345678", new string('a', 91))));
        Assert.NotNull(_sender.Check(Sms("+819012345678", new string('가', 46))));
    }

    [Fact]
    public void Check_ABodyWithCharactersOutsideEucKr_IsRefused()
    {
        Assert.NotNull(_sender.Check(Sms("+821012345678", "Hello 👋")));
    }

    private static string Failed(string statusCode, string statusMessage) =>
        JsonSerializer.Serialize(new
        {
            groupInfo = new { count = new { total = 1, registeredFailed = 1 } },
            failedMessageList = new[] { new { to = "01012345678", statusCode, statusMessage } }
        });

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private HttpStatusCode _status = HttpStatusCode.OK;
        private string _response = """{"groupInfo":{"count":{"total":1,"registeredFailed":0}},"failedMessageList":[]}""";

        public HttpRequestMessage? Request { get; private set; }

        public JsonDocument? Body { get; private set; }

        public void Respond(HttpStatusCode status, string body)
        {
            _status = status;
            _response = body;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            Body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(_status) { Content = new StringContent(_response, Encoding.UTF8, "application/json") };
        }
    }
}
