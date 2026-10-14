using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sendway.Core;
using Xunit;

namespace Sendway.Core.Tests;

public class MessageDeliveryProcessorTests : IAsyncLifetime
{
    private readonly TestDatabase _database = new();
    private readonly ManualTimeProvider _time = new();
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly MessageDeliveryOptions _options = new();
    private readonly ScriptedEmailSender _emailSender = new();
    private readonly ScriptedPushSender _pushSender = new();
    private readonly ScriptedSmsSender _smsSender = new();
    private MessageOutbox _outbox = null!;
    private MessageDeliveryProcessor _processor = null!;

    public async Task InitializeAsync()
    {
        await _database.CreateAsync();
        _outbox = new MessageOutbox(_database.Factory, new EphemeralDataProtectionProvider(), _time, new WebhookDeliverySignal());
        _processor = new MessageDeliveryProcessor(
            _outbox,
            _emailSender,
            _pushSender,
            _smsSender,
            Options.Create(_options),
            new MessageDeliverySignal(),
            NullLogger<MessageDeliveryProcessor>.Instance);
    }

    public Task DisposeAsync()
    {
        _database.Dispose();
        return Task.CompletedTask;
    }

    private async Task<Guid> EnqueueEmailAsync()
    {
        var message = new EmailMessage(_tenantId, ["user@example.com"], "Subject", "Body");
        var result = await _outbox.EnqueueAsync(
            new MessageSubmission(_tenantId, MessageChannels.Email, "user@example.com", MessagePayload.Serialize(message)));
        return result.Record.Id;
    }

    private async Task<MessageRecord> StoredAsync(Guid id) => (await _outbox.GetAsync(id))!;

    [Fact]
    public async Task ProcessNext_WhenNothingIsDue_ReturnsFalse()
    {
        Assert.False(await _processor.ProcessNextAsync());
    }

    [Fact]
    public async Task ProcessNext_DeliversAnEmailWithEverythingTheRequestCarried()
    {
        var message = new EmailMessage(
            _tenantId,
            ["to1@example.com", "to2@example.com"],
            "The Subject",
            "The Body",
            cc: ["cc@example.com"],
            bcc: ["bcc@example.com"],
            htmlBody: "<p>The Body</p>");
        var enqueued = await _outbox.EnqueueAsync(
            new MessageSubmission(_tenantId, MessageChannels.Email, "to1@example.com, to2@example.com", MessagePayload.Serialize(message)));

        var processed = await _processor.ProcessNextAsync();

        Assert.True(processed);
        var sent = Assert.Single(_emailSender.Sent);
        Assert.Equal(_tenantId, sent.TenantId);
        Assert.Equal(["to1@example.com", "to2@example.com"], sent.To);
        Assert.Equal(["cc@example.com"], sent.Cc);
        Assert.Equal(["bcc@example.com"], sent.Bcc);
        Assert.Equal("The Subject", sent.Subject);
        Assert.Equal("The Body", sent.Body);
        Assert.Equal("<p>The Body</p>", sent.HtmlBody);

        var stored = await StoredAsync(enqueued.Record.Id);
        Assert.Equal(DeliveryStatus.Sent, stored.Status);
        Assert.Equal(1, stored.AttemptCount);
    }

    [Fact]
    public async Task ProcessNext_DeliversAPush()
    {
        var message = new PushMessage(_tenantId, "device-token-123", "The Title", "The Body");
        var enqueued = await _outbox.EnqueueAsync(
            new MessageSubmission(_tenantId, MessageChannels.Push, "device-token-123", MessagePayload.Serialize(message)));

        await _processor.ProcessNextAsync();

        var sent = Assert.Single(_pushSender.Sent);
        Assert.Equal(_tenantId, sent.TenantId);
        Assert.Equal("device-token-123", sent.DeviceToken);
        Assert.Equal("The Title", sent.Title);
        Assert.Equal("The Body", sent.Body);
        Assert.Equal(DeliveryStatus.Sent, (await StoredAsync(enqueued.Record.Id)).Status);
    }

    [Fact]
    public async Task ProcessNext_DeliversAnSms()
    {
        var message = new SmsMessage(_tenantId, "+821012345678", "The Body");
        var enqueued = await _outbox.EnqueueAsync(
            new MessageSubmission(_tenantId, MessageChannels.Sms, "+821012345678", MessagePayload.Serialize(message)));

        await _processor.ProcessNextAsync();

        var sent = Assert.Single(_smsSender.Sent);
        Assert.Equal(_tenantId, sent.TenantId);
        Assert.Equal("+821012345678", sent.To.ToString());
        Assert.Equal("The Body", sent.Body);
        Assert.Equal(DeliveryStatus.Sent, (await StoredAsync(enqueued.Record.Id)).Status);
    }

    [Fact]
    public async Task ProcessNext_WhenTheProviderRejectsTheMessage_FailsWithoutRetrying()
    {
        _smsSender.FailWith(new MessageRejectedException("the recipient opted out"));
        var message = new SmsMessage(_tenantId, "+821012345678", "The Body");
        var enqueued = await _outbox.EnqueueAsync(
            new MessageSubmission(_tenantId, MessageChannels.Sms, "+821012345678", MessagePayload.Serialize(message)));

        await _processor.ProcessNextAsync();

        var stored = await StoredAsync(enqueued.Record.Id);
        Assert.Equal(DeliveryStatus.Failed, stored.Status);
        Assert.Equal("the recipient opted out", stored.Error);
        Assert.Equal(1, stored.AttemptCount);
    }

    [Fact]
    public async Task ProcessNext_WhenTheDestinationIsRejected_FailsWithoutRetrying()
    {
        _emailSender.FailWith(new InvalidRecipientException("the mailbox does not exist"));
        var id = await EnqueueEmailAsync();

        await _processor.ProcessNextAsync();

        var stored = await StoredAsync(id);
        Assert.Equal(DeliveryStatus.Failed, stored.Status);
        Assert.Equal("the mailbox does not exist", stored.Error);
        Assert.Equal(1, stored.AttemptCount);
        Assert.False(await _processor.ProcessNextAsync());
    }

    [Fact]
    public async Task ProcessNext_WhenAnAttemptFails_RetriesAfterTheBaseDelay()
    {
        _emailSender.FailWith(new InvalidOperationException("connection refused"));
        var id = await EnqueueEmailAsync();

        await _processor.ProcessNextAsync();

        var afterFailure = await StoredAsync(id);
        Assert.Equal(DeliveryStatus.Queued, afterFailure.Status);
        Assert.Equal("connection refused", afterFailure.Error);
        Assert.Equal(_time.GetUtcNow() + _options.RetryBaseDelay, afterFailure.NextAttemptAt);

        // Not due yet: nothing is attempted before the delay has passed.
        Assert.False(await _processor.ProcessNextAsync());

        _emailSender.Succeed();
        _time.Advance(_options.RetryBaseDelay);
        Assert.True(await _processor.ProcessNextAsync());

        var afterRetry = await StoredAsync(id);
        Assert.Equal(DeliveryStatus.Sent, afterRetry.Status);
        Assert.Equal(2, afterRetry.AttemptCount);
        Assert.Null(afterRetry.Error);
    }

    [Fact]
    public async Task ProcessNext_SpacesRetriesFurtherApartEachTime()
    {
        _emailSender.FailWith(new InvalidOperationException("connection refused"));
        var id = await EnqueueEmailAsync();
        var delays = new List<TimeSpan>();

        for (var attempt = 1; attempt < _options.MaxAttempts; attempt++)
        {
            await _processor.ProcessNextAsync();
            var delay = (await StoredAsync(id)).NextAttemptAt!.Value - _time.GetUtcNow();
            delays.Add(delay);
            _time.Advance(delay);
        }

        Assert.Equal(
            [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(18)],
            delays);
    }

    [Fact]
    public async Task ProcessNext_WhenEveryAttemptFails_FailsAfterTheLastOne()
    {
        _emailSender.FailWith(new InvalidOperationException("connection refused"));
        var id = await EnqueueEmailAsync();

        for (var attempt = 1; attempt <= _options.MaxAttempts; attempt++)
        {
            Assert.True(await _processor.ProcessNextAsync());
            _time.Advance(TimeSpan.FromHours(1));
        }

        var stored = await StoredAsync(id);
        Assert.Equal(DeliveryStatus.Failed, stored.Status);
        Assert.Equal("connection refused", stored.Error);
        Assert.Equal(_options.MaxAttempts, stored.AttemptCount);
        Assert.Equal(_options.MaxAttempts, _emailSender.CallCount);
        Assert.False(await _processor.ProcessNextAsync());
    }

    [Fact]
    public async Task ProcessNext_WhenTheProviderDoesNotAnswerInTime_AbandonsTheAttemptAndRetries()
    {
        _options.AttemptTimeout = TimeSpan.FromMilliseconds(50);
        _emailSender.HangUntilCancelled();
        var id = await EnqueueEmailAsync();

        await _processor.ProcessNextAsync();

        var stored = await StoredAsync(id);
        Assert.Equal(DeliveryStatus.Queued, stored.Status);
        Assert.Contains("No response within", stored.Error);
        Assert.Equal(1, stored.AttemptCount);
    }

    [Fact]
    public async Task ProcessNext_WhenShutdownInterruptsAnAttempt_ReturnsTheMessageUncounted()
    {
        _emailSender.HangUntilCancelled();
        var id = await EnqueueEmailAsync();
        using var shutdown = new CancellationTokenSource();

        var processing = _processor.ProcessNextAsync(shutdown.Token);
        await _emailSender.Started;
        shutdown.Cancel();
        await processing;

        var stored = await StoredAsync(id);
        Assert.Equal(DeliveryStatus.Queued, stored.Status);
        Assert.Equal(0, stored.AttemptCount);
        Assert.Null(stored.Error);
    }

    [Fact]
    public async Task ProcessNext_WhenEarlierAttemptsNeverReportedBack_StopsAtTheAttemptLimit()
    {
        var id = await EnqueueEmailAsync();
        var lease = TimeSpan.FromMinutes(5);

        // Each claim here stands for a worker that died mid-attempt: nothing is recorded, and the
        // message only becomes claimable again once its lease runs out.
        for (var attempt = 1; attempt <= _options.MaxAttempts; attempt++)
        {
            await _outbox.ClaimNextAsync(lease);
            _time.Advance(lease);
        }

        Assert.True(await _processor.ProcessNextAsync());

        var stored = await StoredAsync(id);
        Assert.Equal(DeliveryStatus.Failed, stored.Status);
        Assert.Equal(0, _emailSender.CallCount);
    }

    private sealed class ScriptedEmailSender : IEmailSender
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Exception? _failure;
        private bool _hang;

        public List<EmailMessage> Sent { get; } = [];

        public int CallCount { get; private set; }

        public Task Started => _started.Task;

        public void FailWith(Exception failure) => _failure = failure;

        public void Succeed() => _failure = null;

        public void HangUntilCancelled() => _hang = true;

        public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
        {
            CallCount++;
            _started.TrySetResult();

            if (_hang)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            if (_failure is not null)
            {
                throw _failure;
            }

            Sent.Add(message);
        }
    }

    private sealed class ScriptedSmsSender : ISmsSender
    {
        private Exception? _failure;

        public List<SmsMessage> Sent { get; } = [];

        public void FailWith(Exception failure) => _failure = failure;

        public string? Check(SmsMessage message) => null;

        public Task SendAsync(SmsMessage message, CancellationToken cancellationToken = default)
        {
            if (_failure is not null)
            {
                throw _failure;
            }

            Sent.Add(message);
            return Task.CompletedTask;
        }
    }

    private sealed class ScriptedPushSender : IPushSender
    {
        public List<PushMessage> Sent { get; } = [];

        public Task SendAsync(PushMessage message, CancellationToken cancellationToken = default)
        {
            Sent.Add(message);
            return Task.CompletedTask;
        }
    }
}
