using System.Net;
using System.Text;

namespace Sendway.Client.Tests;

public class ClientTests
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

    private static HttpResponseMessage Json(HttpStatusCode status, string json, string mediaType = "application/json") =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, mediaType) };

    [Fact]
    public async Task Send_PostsUnderTheBaseAddressWithTheKeyAndTheIdempotencyKey()
    {
        var client = Client(_ => Json(HttpStatusCode.Accepted, """{"id":"3f2b1c4e-0000-4000-8000-000000000001","status":"Queued"}"""), out var seen);

        var accepted = await client.SendEmailAsync(new EmailMessage(["user@example.com"], "Subject", "Body"), "key-1");

        var request = Assert.Single(seen);
        Assert.Equal("https://sendway.example/base/messages/email", request.RequestUri!.ToString());
        Assert.Equal("sw_key", request.Headers.GetValues("X-Api-Key").Single());
        Assert.Equal("key-1", request.Headers.GetValues("Idempotency-Key").Single());
        Assert.Equal(MessageState.Queued, accepted.Status);
    }

    [Fact]
    public async Task Send_LeavesUnsetOptionalFieldsOutOfTheBody()
    {
        string? body = null;
        var client = Client(request =>
        {
            body = request.Content!.ReadAsStringAsync().Result;
            return Json(HttpStatusCode.Accepted, """{"id":"3f2b1c4e-0000-4000-8000-000000000001","status":"Queued"}""");
        }, out _);

        await client.SendSmsAsync(new SmsMessage("+821012345678", "Body"));

        using var sent = System.Text.Json.JsonDocument.Parse(body!);
        Assert.Equal(["to", "body"], sent.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.Equal("+821012345678", sent.RootElement.GetProperty("to").GetString());
    }

    [Fact]
    public async Task ARateLimitedCall_SaysHowLongToWait()
    {
        var client = Client(_ =>
        {
            var response = Json(HttpStatusCode.TooManyRequests, """{"status":429,"detail":"Too many requests.","code":"rate_limited"}""", "application/problem+json");
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(12));
            return response;
        }, out _);

        var refused = await Assert.ThrowsAsync<SendwayException>(() => client.GetMessageAsync(Guid.NewGuid()));

        Assert.Equal(SendwayErrorCode.RateLimited, refused.Code);
        Assert.Equal(TimeSpan.FromSeconds(12), refused.RetryAfter);
        Assert.Equal("Too many requests.", refused.Message);
    }

    [Fact]
    public async Task AnErrorFromSomethingInFrontOfSendway_IsStillAnException()
    {
        var client = Client(_ => Json(HttpStatusCode.BadGateway, "<html>bad gateway</html>", "text/html"), out _);

        var refused = await Assert.ThrowsAsync<SendwayException>(() => client.GetMessageAsync(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.BadGateway, refused.Status);
        Assert.Equal(SendwayErrorCode.Unknown, refused.Code);
        Assert.Null(refused.CodeText);
    }

    [Fact]
    public async Task RefusedRecipients_AreReadWithTheServersReply()
    {
        var client = Client(_ => Json(HttpStatusCode.OK,
            """{"id":"3f2b1c4e-0000-4000-8000-000000000001","channel":"email","recipient":"a@b.c, typo@b.c","status":"Sent","error":null,"reason":null,"refusedRecipients":[{"address":"typo@b.c","error":"550 5.1.1 no such user"}],"attempts":1,"acceptedAt":"2026-10-15T12:00:00Z","completedAt":"2026-10-15T12:00:01Z"}"""), out _);

        var message = await client.GetMessageAsync(Guid.NewGuid());

        Assert.Equal(MessageState.Sent, message.Status);
        Assert.Equal(new RefusedRecipient("typo@b.c", "550 5.1.1 no such user"), Assert.Single(message.RefusedRecipients!));
    }

    [Fact]
    public async Task ValuesThisVersionDoesNotKnow_ReadAsUnknownWithTheTextKept()
    {
        var client = Client(_ => Json(HttpStatusCode.OK,
            """{"id":"3f2b1c4e-0000-4000-8000-000000000001","channel":"email","recipient":"a@b.c","status":"Archived","error":"x","reason":"something_new","attempts":1,"acceptedAt":"2026-10-15T12:00:00Z","completedAt":null}"""), out _);

        var message = await client.GetMessageAsync(Guid.NewGuid());

        Assert.Equal(MessageState.Unknown, message.Status);
        Assert.Equal("Archived", message.StatusText);
        Assert.Equal(FailureReason.Unknown, message.Reason);
        Assert.Equal("something_new", message.ReasonText);
    }

    private sealed class Answering(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(answer(request));
    }
}
