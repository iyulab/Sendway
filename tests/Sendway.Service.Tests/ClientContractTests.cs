using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sendway.Client;
using Sendway.Core;
using ClientSms = Sendway.Client.SmsMessage;
using CoreMessageEvent = Sendway.Core.MessageEvent;
using CoreSms = Sendway.Core.SmsMessage;

namespace Sendway.Service.Tests;

// The published client against the running service: a change on either side that breaks the contract between them
// fails here, not in a calling service.
public class ClientContractTests : IClassFixture<WebApplicationFactory<Program>>, IAsyncLifetime
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly ScriptedSmsSender _smsSender = new();
    private readonly TestIsolatedStorage _storage = new();
    private SendwayClient _client = null!;

    public ClientContractTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((context, configuration) => _storage.Apply(context, configuration));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ISmsSender>();
                services.AddSingleton<ISmsSender>(_smsSender);
            });
        });
    }

    public async Task InitializeAsync()
    {
        var (_, apiKey) = await TestTenantSeeder.SeedAsync(_factory);
        _client = new SendwayClient(_factory.CreateClient(), apiKey);
    }

    public Task DisposeAsync()
    {
        _storage.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task SendThenRead_ReportsTheOutcomeInTheClientsTypes()
    {
        var accepted = await _client.SendSmsAsync(new ClientSms("+821012345678", "Your code is 123456")
        {
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(3)
        });
        Assert.Equal(MessageState.Queued, accepted.Status);

        await TestDelivery.DeliverPendingAsync(_factory);

        var message = await _client.GetMessageAsync(accepted.Id);
        Assert.Equal(MessageState.Sent, message.Status);
        Assert.Equal("sms", message.Channel);
        Assert.Equal("+821012345678", message.Recipient);
        Assert.Null(message.Reason);
        Assert.Equal("Your code is 123456", _smsSender.LastMessage!.Body);
    }

    [Fact]
    public async Task AFailedMessage_CarriesItsReasonAsTheClientsValue()
    {
        _smsSender.FailWith(new MessageRejectedException("opted out", MessageFailureReasons.RecipientRefused));
        var accepted = await _client.SendSmsAsync(new ClientSms("+821012345678", "Body"));

        await TestDelivery.DeliverPendingAsync(_factory);

        var message = await _client.GetMessageAsync(accepted.Id);
        Assert.Equal(MessageState.Failed, message.Status);
        Assert.Equal(FailureReason.RecipientRefused, message.Reason);
    }

    [Fact]
    public async Task TheSameIdempotencyKey_ReturnsTheSameMessage_AndADifferentRequestIsRefusedWithItsCode()
    {
        var first = await _client.SendSmsAsync(new ClientSms("+821012345678", "Body"), "key-1");
        var again = await _client.SendSmsAsync(new ClientSms("+821012345678", "Body"), "key-1");
        var refused = await Assert.ThrowsAsync<SendwayException>(() =>
            _client.SendSmsAsync(new ClientSms("+821012345678", "Another body"), "key-1"));

        Assert.Equal(first.Id, again.Id);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.Status);
        Assert.Equal(SendwayErrorCode.IdempotencyKeyReused, refused.Code);
    }

    [Fact]
    public async Task ARefusal_BecomesATypedException()
    {
        var malformed = await Assert.ThrowsAsync<SendwayException>(() => _client.SendSmsAsync(new ClientSms("010-1234-5678", "Body")));
        var unknown = await Assert.ThrowsAsync<SendwayException>(() => _client.GetMessageAsync(Guid.NewGuid()));
        var unauthorized = await Assert.ThrowsAsync<SendwayException>(() =>
            new SendwayClient(_factory.CreateClient(), "sw_not-a-key").GetMessageAsync(Guid.NewGuid()));

        Assert.Equal(SendwayErrorCode.InvalidRecipient, malformed.Code);
        Assert.Equal(SendwayErrorCode.NotFound, unknown.Code);
        Assert.Equal(SendwayErrorCode.Unauthorized, unauthorized.Code);
    }

    [Fact]
    public void AnEventTheServiceSigns_VerifiesAndReadsWithTheClient()
    {
        var secret = "whsec_" + Convert.ToBase64String(new byte[24]);
        var record = new MessageRecord
        {
            Id = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            Channel = MessageChannels.Sms,
            Recipient = "+821012345678",
            Status = DeliveryStatus.Failed,
            Error = "the recipient opted out",
            FailureReason = MessageFailureReasons.RecipientRefused,
            AttemptCount = 1,
            AcceptedAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow
        };
        var body = CoreMessageEvent.Serialize(record, DateTimeOffset.UtcNow);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var received = new SendwayWebhook(secret).Verify(
            "evt-1", timestamp.ToString(), WebhookSignature.Compute(secret, "evt-1", timestamp, body), body);

        Assert.Equal(Sendway.Client.MessageEvent.Failed, received.TypeText);
        Assert.Equal(record.Id, received.Data.Id);
        Assert.Equal(FailureReason.RecipientRefused, received.Data.Reason);
    }

    private sealed class ScriptedSmsSender : ISmsSender
    {
        private Exception? _failure;

        public CoreSms? LastMessage { get; private set; }

        public void FailWith(Exception failure) => _failure = failure;

        public string? Check(CoreSms message) => null;

        public Task SendAsync(CoreSms message, CancellationToken cancellationToken = default)
        {
            if (_failure is not null)
            {
                throw _failure;
            }

            LastMessage = message;
            return Task.CompletedTask;
        }
    }
}
