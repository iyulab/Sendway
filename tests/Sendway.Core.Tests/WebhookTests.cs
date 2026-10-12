using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sendway.Core;
using Xunit;

namespace Sendway.Core.Tests;

public class WebhookSignatureTests
{
    // The worked example published with the Standard Webhooks reference libraries: a receiver using
    // any of them must arrive at this same value.
    [Fact]
    public void Compute_MatchesTheStandardWebhooksReferenceExample()
    {
        var signature = WebhookSignature.Compute(
            secret: "whsec_MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw",
            id: "msg_p5jXN8AQM9LWM0D4loKWxJek",
            unixTimeSeconds: 1614265330,
            body: "{\"test\": 2432232314}");

        Assert.Equal("v1,g0hM9SsE+OTPJTGt/tmIKtSyZlE3uFJELVlNIOLJ1OE=", signature);
    }
}

public class HttpWebhookSenderTests
{
    private const string Secret = "whsec_MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw";

    private static (HttpWebhookSender Sender, StubHandler Handler, ManualTimeProvider Time) Create(HttpStatusCode status)
    {
        var handler = new StubHandler(status);
        var time = new ManualTimeProvider();
        return (new HttpWebhookSender(new HttpClient(handler), time), handler, time);
    }

    [Fact]
    public async Task Send_PostsTheBodyWithAVerifiableSignature()
    {
        var (sender, handler, time) = Create(HttpStatusCode.OK);
        var eventId = Guid.NewGuid();

        await sender.SendAsync(new Uri("https://receiver.example.com/hooks"), Secret, eventId, "{\"type\":\"message.sent\"}");

        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("https://receiver.example.com/hooks", handler.Url);
        Assert.Equal("{\"type\":\"message.sent\"}", handler.Body);
        Assert.Equal("application/json", handler.ContentType);
        Assert.Equal(eventId.ToString(), handler.Header(WebhookSignature.IdHeader));

        var timestamp = time.GetUtcNow().ToUnixTimeSeconds();
        Assert.Equal(timestamp.ToString(), handler.Header(WebhookSignature.TimestampHeader));
        Assert.Equal(
            WebhookSignature.Compute(Secret, eventId.ToString(), timestamp, handler.Body!),
            handler.Header(WebhookSignature.SignatureHeader));
    }

    [Theory]
    [InlineData(HttpStatusCode.NoContent)]
    [InlineData(HttpStatusCode.Accepted)]
    public async Task Send_TreatsAny2xxAsDelivered(HttpStatusCode status)
    {
        var (sender, _, _) = Create(status);

        await sender.SendAsync(new Uri("https://receiver.example.com/hooks"), Secret, Guid.NewGuid(), "{}");
    }

    [Fact]
    public async Task Send_WhenTheEndpointErrors_FailsSoTheAttemptCanBeRetried()
    {
        var (sender, _, _) = Create(HttpStatusCode.InternalServerError);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            sender.SendAsync(new Uri("https://receiver.example.com/hooks"), Secret, Guid.NewGuid(), "{}"));

        Assert.Contains("500", exception.Message);
    }

    [Fact]
    public async Task Send_WhenTheEndpointAnswersGone_ReportsARejection()
    {
        var (sender, _, _) = Create(HttpStatusCode.Gone);

        await Assert.ThrowsAsync<WebhookRejectedException>(() =>
            sender.SendAsync(new Uri("https://receiver.example.com/hooks"), Secret, Guid.NewGuid(), "{}"));
    }

    private sealed class StubHandler(HttpStatusCode status) : HttpMessageHandler
    {
        private HttpRequestMessage? _request;

        public HttpMethod? Method => _request?.Method;

        public string? Url => _request?.RequestUri?.AbsoluteUri;

        public string? Body { get; private set; }

        public string? ContentType => _request?.Content?.Headers.ContentType?.MediaType;

        public string? Header(string name) => _request!.Headers.GetValues(name).Single();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _request = request;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status);
        }
    }
}

public class WebhookDeliveryTests : IAsyncLifetime
{
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(1);
    private static readonly Uri EndpointUrl = new("https://receiver.example.com/hooks");

    private readonly TestDatabase _database = new();
    private readonly ManualTimeProvider _time = new();
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly WebhookDeliveryOptions _options = new();
    private readonly ScriptedWebhookSender _sender = new();
    private MessageOutbox _messages = null!;
    private WebhookEndpointStore _endpoints = null!;
    private WebhookDeliveryProcessor _processor = null!;

    public async Task InitializeAsync()
    {
        await _database.CreateAsync();

        var dataProtection = new EphemeralDataProtectionProvider();
        var signal = new WebhookDeliverySignal();
        _messages = new MessageOutbox(_database.Factory, dataProtection, _time, signal);
        _endpoints = new WebhookEndpointStore(_database.Factory, dataProtection, _time);
        _processor = new WebhookDeliveryProcessor(
            new WebhookOutbox(_database.Factory, _time),
            _endpoints,
            _sender,
            Options.Create(_options),
            signal,
            NullLogger<WebhookDeliveryProcessor>.Instance);
    }

    public Task DisposeAsync()
    {
        _database.Dispose();
        return Task.CompletedTask;
    }

    private async Task<ClaimedMessage> ClaimMessageAsync()
    {
        await _messages.EnqueueAsync(new MessageSubmission(_tenantId, MessageChannels.Email, "user@example.com", "payload"));
        return (await _messages.ClaimNextAsync(Lease))!;
    }

    private async Task<List<WebhookDelivery>> EventsAsync()
    {
        await using var db = await _database.Factory.CreateDbContextAsync();
        return await db.WebhookDeliveries.AsNoTracking().ToListAsync();
    }

    // --- endpoint registration -------------------------------------------------------------

    [Fact]
    public async Task Endpoint_Set_IssuesASecretThatIsOnlyReadableForDelivery()
    {
        var registered = await _endpoints.SetAsync(_tenantId, EndpointUrl);

        Assert.StartsWith(WebhookSignature.SecretPrefix, registered.Secret);
        Assert.Equal(32, Convert.FromBase64String(registered.Secret[WebhookSignature.SecretPrefix.Length..]).Length);

        var info = await _endpoints.GetAsync(_tenantId);
        Assert.Equal(EndpointUrl, info!.Url);

        var forDelivery = await _endpoints.GetForDeliveryAsync(_tenantId);
        Assert.Equal(registered.Secret, forDelivery!.Secret);

        await using var db = await _database.Factory.CreateDbContextAsync();
        var stored = await db.WebhookEndpoints.AsNoTracking().SingleAsync();
        Assert.DoesNotContain(registered.Secret, stored.EncryptedSecret);
    }

    [Fact]
    public async Task Endpoint_SetAgain_ReplacesTheUrlAndRotatesTheSecret()
    {
        var first = await _endpoints.SetAsync(_tenantId, EndpointUrl);
        var second = await _endpoints.SetAsync(_tenantId, new Uri("https://other.example.com/hooks"));

        Assert.NotEqual(first.Secret, second.Secret);
        var forDelivery = await _endpoints.GetForDeliveryAsync(_tenantId);
        Assert.Equal(new Uri("https://other.example.com/hooks"), forDelivery!.Url);
        Assert.Equal(second.Secret, forDelivery.Secret);
    }

    [Fact]
    public async Task Endpoint_Delete_RemovesIt()
    {
        await _endpoints.SetAsync(_tenantId, EndpointUrl);

        Assert.True(await _endpoints.DeleteAsync(_tenantId));
        Assert.Null(await _endpoints.GetAsync(_tenantId));
        Assert.False(await _endpoints.DeleteAsync(_tenantId));
    }

    // --- events are produced by message outcomes ---------------------------------------------

    [Fact]
    public async Task MessageSent_QueuesASentEventDescribingTheMessage()
    {
        await _endpoints.SetAsync(_tenantId, EndpointUrl);
        var claimed = await ClaimMessageAsync();

        await _messages.MarkSentAsync(claimed);

        var queued = Assert.Single(await EventsAsync());
        Assert.Equal(_tenantId, queued.TenantId);
        Assert.Equal(claimed.Id, queued.MessageId);
        Assert.Equal(MessageEvent.Sent, queued.EventType);
        Assert.Equal(DeliveryStatus.Queued, queued.Status);

        using var body = JsonDocument.Parse(queued.Payload!);
        Assert.Equal("message.sent", body.RootElement.GetProperty("type").GetString());
        Assert.True(body.RootElement.TryGetProperty("timestamp", out _));
        var data = body.RootElement.GetProperty("data");
        Assert.Equal(claimed.Id, data.GetProperty("id").GetGuid());
        Assert.Equal("email", data.GetProperty("channel").GetString());
        Assert.Equal("user@example.com", data.GetProperty("recipient").GetString());
        Assert.Equal("Sent", data.GetProperty("status").GetString());
        Assert.Equal(1, data.GetProperty("attempts").GetInt32());
        Assert.Equal(JsonValueKind.Null, data.GetProperty("error").ValueKind);
        Assert.True(data.TryGetProperty("acceptedAt", out _));
        Assert.NotEqual(JsonValueKind.Null, data.GetProperty("completedAt").ValueKind);
    }

    [Fact]
    public async Task MessageFailed_QueuesAFailedEventCarryingTheError()
    {
        await _endpoints.SetAsync(_tenantId, EndpointUrl);
        var claimed = await ClaimMessageAsync();

        await _messages.MarkFailedAsync(claimed, "the mailbox does not exist");

        var queued = Assert.Single(await EventsAsync());
        Assert.Equal(MessageEvent.Failed, queued.EventType);

        using var body = JsonDocument.Parse(queued.Payload!);
        Assert.Equal("message.failed", body.RootElement.GetProperty("type").GetString());
        Assert.Equal("Failed", body.RootElement.GetProperty("data").GetProperty("status").GetString());
        Assert.Equal("the mailbox does not exist", body.RootElement.GetProperty("data").GetProperty("error").GetString());
    }

    [Fact]
    public async Task MessageOutcome_WithoutARegisteredEndpoint_QueuesNoEvent()
    {
        var claimed = await ClaimMessageAsync();

        await _messages.MarkSentAsync(claimed);

        Assert.Empty(await EventsAsync());
    }

    [Fact]
    public async Task MessageRetry_IsNotAnOutcomeAndQueuesNoEvent()
    {
        await _endpoints.SetAsync(_tenantId, EndpointUrl);
        var claimed = await ClaimMessageAsync();

        await _messages.ScheduleRetryAsync(claimed, "temporarily unreachable", TimeSpan.FromSeconds(5));

        Assert.Empty(await EventsAsync());
    }

    [Fact]
    public async Task StaleMessageOutcome_QueuesNoEvent()
    {
        await _endpoints.SetAsync(_tenantId, EndpointUrl);
        var stale = await ClaimMessageAsync();
        _time.Advance(Lease);
        await _messages.ClaimNextAsync(Lease);

        await _messages.MarkSentAsync(stale);

        Assert.Empty(await EventsAsync());
    }

    // --- event delivery ---------------------------------------------------------------------

    private async Task<WebhookDelivery> QueueEventAsync()
    {
        await _messages.MarkSentAsync(await ClaimMessageAsync());
        return (await EventsAsync()).Single();
    }

    [Fact]
    public async Task Process_DeliversTheEventToTheTenantsEndpoint()
    {
        var registration = await _endpoints.SetAsync(_tenantId, EndpointUrl);
        var queued = await QueueEventAsync();

        Assert.True(await _processor.ProcessNextAsync());

        var call = Assert.Single(_sender.Calls);
        Assert.Equal(EndpointUrl, call.Url);
        Assert.Equal(registration.Secret, call.Secret);
        Assert.Equal(queued.Id, call.EventId);
        Assert.Equal(queued.Payload, call.Body);

        var delivered = (await EventsAsync()).Single();
        Assert.Equal(DeliveryStatus.Sent, delivered.Status);
        Assert.Null(delivered.Payload);
        Assert.False(await _processor.ProcessNextAsync());
    }

    [Fact]
    public async Task Process_WhenTheEndpointIsDown_RetriesWithTheSameEventId()
    {
        await _endpoints.SetAsync(_tenantId, EndpointUrl);
        var queued = await QueueEventAsync();
        _sender.FailWith(new HttpRequestException("The endpoint responded 503."));

        await _processor.ProcessNextAsync();

        var waiting = (await EventsAsync()).Single();
        Assert.Equal(DeliveryStatus.Queued, waiting.Status);
        Assert.Equal("The endpoint responded 503.", waiting.Error);
        Assert.False(await _processor.ProcessNextAsync());

        _sender.Succeed();
        _time.Advance(_options.RetryBaseDelay);
        Assert.True(await _processor.ProcessNextAsync());

        Assert.Equal(DeliveryStatus.Sent, (await EventsAsync()).Single().Status);
        Assert.Equal(2, _sender.Calls.Count);
        Assert.All(_sender.Calls, call => Assert.Equal(queued.Id, call.EventId));
    }

    [Fact]
    public async Task Process_WhenTheEndpointDeclines_FailsWithoutRetrying()
    {
        await _endpoints.SetAsync(_tenantId, EndpointUrl);
        await QueueEventAsync();
        _sender.FailWith(new WebhookRejectedException("The endpoint responded 410 Gone."));

        await _processor.ProcessNextAsync();

        var failed = (await EventsAsync()).Single();
        Assert.Equal(DeliveryStatus.Failed, failed.Status);
        Assert.Equal("The endpoint responded 410 Gone.", failed.Error);
        Assert.Equal(1, failed.AttemptCount);
    }

    [Fact]
    public async Task Process_WhenTheEndpointWasRemovedMeanwhile_FailsTheEvent()
    {
        await _endpoints.SetAsync(_tenantId, EndpointUrl);
        await QueueEventAsync();
        await _endpoints.DeleteAsync(_tenantId);

        await _processor.ProcessNextAsync();

        Assert.Equal(DeliveryStatus.Failed, (await EventsAsync()).Single().Status);
        Assert.Empty(_sender.Calls);
    }

    [Fact]
    public async Task Process_WhenEveryAttemptFails_GivesUpAfterTheLastOne()
    {
        await _endpoints.SetAsync(_tenantId, EndpointUrl);
        await QueueEventAsync();
        _sender.FailWith(new HttpRequestException("The endpoint responded 500."));

        for (var attempt = 1; attempt <= _options.MaxAttempts; attempt++)
        {
            Assert.True(await _processor.ProcessNextAsync());
            _time.Advance(TimeSpan.FromHours(12));
        }

        var failed = (await EventsAsync()).Single();
        Assert.Equal(DeliveryStatus.Failed, failed.Status);
        Assert.Equal(_options.MaxAttempts, failed.AttemptCount);
        Assert.False(await _processor.ProcessNextAsync());
    }

    private sealed record WebhookCall(Uri Url, string Secret, Guid EventId, string Body);

    private sealed class ScriptedWebhookSender : IWebhookSender
    {
        private Exception? _failure;

        public List<WebhookCall> Calls { get; } = [];

        public void FailWith(Exception failure) => _failure = failure;

        public void Succeed() => _failure = null;

        public Task SendAsync(Uri url, string secret, Guid eventId, string body, CancellationToken cancellationToken = default)
        {
            Calls.Add(new WebhookCall(url, secret, eventId, body));
            return _failure is null ? Task.CompletedTask : Task.FromException(_failure);
        }
    }
}
