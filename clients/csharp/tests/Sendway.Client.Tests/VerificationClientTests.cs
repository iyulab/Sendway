using System.Net;
using System.Text;

namespace Sendway.Client.Tests;

public class VerificationClientTests
{
    private static SendwayClient Client(Func<HttpRequestMessage, HttpResponseMessage> answer, out List<HttpRequestMessage> seen)
    {
        var requests = new List<HttpRequestMessage>();
        seen = requests;
        var http = new HttpClient(new Answering(request =>
        {
            requests.Add(request);
            return answer(request);
        }))
        {
            BaseAddress = new Uri("https://sendway.example/base")
        };
        return new SendwayClient(http, "sw_key");
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Start_PostsOnlyWhatWasSet_WithTheKeyAndTheIdempotencyKey()
    {
        string? body = null;
        var client = Client(request =>
        {
            body = request.Content!.ReadAsStringAsync().Result;
            return Json(HttpStatusCode.Accepted, """{"id":"3f2b1c4e-0000-4000-8000-000000000001","expiresAt":"2026-11-11T12:05:00+00:00"}""");
        }, out var seen);

        var started = await client.StartVerificationAsync(new VerificationRequest(VerificationChannel.Sms, "+821012345678") { Purpose = "signup" }, "k-1");

        var request = Assert.Single(seen);
        Assert.Equal("https://sendway.example/base/verifications", request.RequestUri!.ToString());
        Assert.Equal("k-1", request.Headers.GetValues("Idempotency-Key").Single());
        // The serializer may escape characters (a "+" as +); what counts is the JSON, and that nothing unset is in it.
        using var sent = System.Text.Json.JsonDocument.Parse(body!);
        var properties = sent.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString());
        Assert.Equal(new Dictionary<string, string?> { ["channel"] = "sms", ["to"] = "+821012345678", ["purpose"] = "signup" }, properties);
        Assert.Null(started.Code);
    }

    [Fact]
    public async Task Check_PostsTheCode_AndReadsAWrongCodeAsAnAnswerNotAnException()
    {
        string? body = null;
        var client = Client(request =>
        {
            body = request.Content!.ReadAsStringAsync().Result;
            return Json(HttpStatusCode.OK, """{"status":"mismatch","attemptsLeft":2}""");
        }, out var seen);
        var id = Guid.Parse("3f2b1c4e-0000-4000-8000-000000000001");

        var check = await client.CheckVerificationAsync(id, "123456");

        Assert.Equal("https://sendway.example/base/verifications/3f2b1c4e-0000-4000-8000-000000000001/check", Assert.Single(seen).RequestUri!.ToString());
        Assert.Equal("""{"code":"123456"}""", body);
        Assert.Equal(VerificationResult.Mismatch, check.Result);
        Assert.Equal(2, check.AttemptsLeft);
    }

    [Theory]
    [InlineData("verified", VerificationResult.Verified)]
    [InlineData("expired", VerificationResult.Expired)]
    [InlineData("too_many_attempts", VerificationResult.TooManyAttempts)]
    [InlineData("something_new", VerificationResult.Unknown)]
    public async Task ACheckResultTheClientDoesNotKnow_ReadsAsUnknown_WithTheTextKept(string status, VerificationResult expected)
    {
        var client = Client(_ => Json(HttpStatusCode.OK, $$"""{"status":"{{status}}"}"""), out _);

        var check = await client.CheckVerificationAsync(Guid.NewGuid(), "1");

        Assert.Equal(expected, check.Result);
        Assert.Equal(status, check.StatusText);
    }

    [Fact]
    public async Task Get_ReadsTheStateAndTheDelivery()
    {
        var client = Client(_ => Json(HttpStatusCode.OK, """
            {"id":"3f2b1c4e-0000-4000-8000-000000000001","channel":"sms","purpose":"","status":"pending",
             "createdAt":"2026-11-11T12:00:00+00:00","expiresAt":"2026-11-11T12:05:00+00:00","attemptsLeft":5,
             "delivery":{"status":"Failed","reason":"recipient_refused"}}
            """), out _);

        var verification = await client.GetVerificationAsync(Guid.NewGuid());

        Assert.Equal(VerificationState.Pending, verification.State);
        Assert.Equal(MessageState.Failed, verification.Delivery.Status);
        Assert.Equal(FailureReason.RecipientRefused, verification.Delivery.Reason);
    }

    [Fact]
    public async Task AnAskedTooSoon_IsAnExceptionWithTheCodeAndRetryAfter()
    {
        var client = Client(_ =>
        {
            var response = Json((HttpStatusCode)429, """{"status":429,"code":"destination_rate_limited","detail":"A code was sent a moment ago."}""");
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(12));
            return response;
        }, out _);

        var refused = await Assert.ThrowsAsync<SendwayException>(() => client.StartVerificationAsync(new VerificationRequest(VerificationChannel.Email, "a@example.com")));

        Assert.Equal(SendwayErrorCode.DestinationRateLimited, refused.Code);
        Assert.Equal(TimeSpan.FromSeconds(12), refused.RetryAfter);
    }

    private sealed class Answering(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(answer(request));
    }
}
