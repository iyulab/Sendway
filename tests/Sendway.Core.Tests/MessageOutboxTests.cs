using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Sendway.Core;
using Xunit;

namespace Sendway.Core.Tests;

public class MessageOutboxTests : IAsyncLifetime
{
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(1);

    private readonly TestDatabase _database = new();
    private readonly ManualTimeProvider _time = new();
    private readonly Guid _tenantId = Guid.NewGuid();
    private MessageOutbox _outbox = null!;

    public async Task InitializeAsync()
    {
        await _database.CreateAsync();
        _outbox = new MessageOutbox(_database.Factory, new EphemeralDataProtectionProvider(), _time, new WebhookDeliverySignal());
    }

    public Task DisposeAsync()
    {
        _database.Dispose();
        return Task.CompletedTask;
    }

    private MessageSubmission Submission(string payload = "payload", string? key = null, Guid? tenantId = null) =>
        new(tenantId ?? _tenantId, MessageChannels.Email, "user@example.com", payload, key);

    private async Task<MessageRecord> StoredAsync(Guid id) => (await _outbox.GetAsync(id))!;

    private async Task<int> CountAsync()
    {
        await using var db = await _database.Factory.CreateDbContextAsync();
        return await db.MessageRecords.CountAsync();
    }

    [Fact]
    public async Task Enqueue_StoresQueuedMessage()
    {
        var result = await _outbox.EnqueueAsync(Submission());

        var stored = await StoredAsync(result.Record.Id);
        Assert.Equal(EnqueueOutcome.Accepted, result.Outcome);
        Assert.Equal(DeliveryStatus.Queued, stored.Status);
        Assert.Equal(_tenantId, stored.TenantId);
        Assert.Equal(MessageChannels.Email, stored.Channel);
        Assert.Equal("user@example.com", stored.Recipient);
        Assert.Equal(0, stored.AttemptCount);
        Assert.Equal(_time.GetUtcNow(), stored.AcceptedAt);
        Assert.Null(stored.CompletedAt);
    }

    [Fact]
    public async Task Enqueue_DoesNotStorePayloadInPlaintext()
    {
        var result = await _outbox.EnqueueAsync(Submission(payload: "one-time-code-482913"));

        var stored = await StoredAsync(result.Record.Id);
        Assert.NotNull(stored.Payload);
        Assert.DoesNotContain("482913", stored.Payload);
    }

    [Fact]
    public async Task Enqueue_SameKeyAndRequest_ReplaysTheOriginalMessage()
    {
        var first = await _outbox.EnqueueAsync(Submission(key: "key-1"));
        var second = await _outbox.EnqueueAsync(Submission(key: "key-1"));

        Assert.Equal(EnqueueOutcome.Accepted, first.Outcome);
        Assert.Equal(EnqueueOutcome.Replayed, second.Outcome);
        Assert.Equal(first.Record.Id, second.Record.Id);
        Assert.Equal(1, await CountAsync());
    }

    [Fact]
    public async Task Enqueue_SameKeyWhileFirstIsBeingDelivered_StillReplays()
    {
        var first = await _outbox.EnqueueAsync(Submission(key: "key-1"));
        await _outbox.ClaimNextAsync(Lease);

        var second = await _outbox.EnqueueAsync(Submission(key: "key-1"));

        Assert.Equal(EnqueueOutcome.Replayed, second.Outcome);
        Assert.Equal(first.Record.Id, second.Record.Id);
        Assert.Equal(DeliveryStatus.Sending, second.Record.Status);
        Assert.Equal(1, await CountAsync());
    }

    [Fact]
    public async Task Enqueue_SameKeyDifferentRequest_ReportsKeyReused()
    {
        await _outbox.EnqueueAsync(Submission(payload: "first", key: "key-1"));

        var second = await _outbox.EnqueueAsync(Submission(payload: "second", key: "key-1"));

        Assert.Equal(EnqueueOutcome.KeyReused, second.Outcome);
        Assert.Equal(1, await CountAsync());
    }

    [Fact]
    public async Task Enqueue_SameKeyDifferentTenants_AcceptsBoth()
    {
        var first = await _outbox.EnqueueAsync(Submission(key: "shared-key"));
        var second = await _outbox.EnqueueAsync(Submission(key: "shared-key", tenantId: Guid.NewGuid()));

        Assert.Equal(EnqueueOutcome.Accepted, first.Outcome);
        Assert.Equal(EnqueueOutcome.Accepted, second.Outcome);
        Assert.NotEqual(first.Record.Id, second.Record.Id);
    }

    [Fact]
    public async Task Enqueue_WithoutKey_NeverDeduplicates()
    {
        var first = await _outbox.EnqueueAsync(Submission());
        var second = await _outbox.EnqueueAsync(Submission());

        Assert.Equal(EnqueueOutcome.Accepted, second.Outcome);
        Assert.NotEqual(first.Record.Id, second.Record.Id);
    }

    [Fact]
    public async Task Enqueue_SameKeyAfterItsLifetime_AcceptsANewMessage()
    {
        var first = await _outbox.EnqueueAsync(Submission(key: "key-1"));
        _time.Advance(MessageOutbox.IdempotencyKeyLifetime);

        var second = await _outbox.EnqueueAsync(Submission(key: "key-1"));

        Assert.Equal(EnqueueOutcome.Accepted, second.Outcome);
        Assert.NotEqual(first.Record.Id, second.Record.Id);
        Assert.Equal(2, await CountAsync());
    }

    [Fact]
    public async Task Enqueue_ConcurrentRequestsWithSameKey_CreateOneMessage()
    {
        var results = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => Task.Run(() => _outbox.EnqueueAsync(Submission(key: "racing-key")))));

        Assert.Single(results.Select(r => r.Record.Id).Distinct());
        Assert.Single(results, r => r.Outcome == EnqueueOutcome.Accepted);
        Assert.Equal(1, await CountAsync());
    }

    [Fact]
    public async Task ClaimNext_ReturnsTheDueMessageAndMarksItSending()
    {
        var enqueued = await _outbox.EnqueueAsync(Submission(payload: "the payload"));

        var claimed = await _outbox.ClaimNextAsync(Lease);

        Assert.NotNull(claimed);
        Assert.Equal(enqueued.Record.Id, claimed!.Id);
        Assert.Equal(_tenantId, claimed.TenantId);
        Assert.Equal(MessageChannels.Email, claimed.Channel);
        Assert.Equal("the payload", claimed.Payload);
        Assert.Equal(1, claimed.AttemptCount);

        var stored = await StoredAsync(claimed.Id);
        Assert.Equal(DeliveryStatus.Sending, stored.Status);
        Assert.Equal(1, stored.AttemptCount);
    }

    [Fact]
    public async Task ClaimNext_WhenNothingIsQueued_ReturnsNull()
    {
        Assert.Null(await _outbox.ClaimNextAsync(Lease));
    }

    [Fact]
    public async Task ClaimNext_DoesNotHandOutAMessageThatIsAlreadyClaimed()
    {
        await _outbox.EnqueueAsync(Submission());
        await _outbox.ClaimNextAsync(Lease);

        Assert.Null(await _outbox.ClaimNextAsync(Lease));
    }

    [Fact]
    public async Task ClaimNext_ClaimsOldestDueMessageFirst()
    {
        var older = await _outbox.EnqueueAsync(Submission(payload: "older"));
        _time.Advance(TimeSpan.FromSeconds(1));
        await _outbox.EnqueueAsync(Submission(payload: "newer"));

        var claimed = await _outbox.ClaimNextAsync(Lease);

        Assert.Equal(older.Record.Id, claimed!.Id);
    }

    [Fact]
    public async Task ClaimNext_AfterTheLeaseExpires_HandsTheMessageOutAgain()
    {
        await _outbox.EnqueueAsync(Submission());
        var first = await _outbox.ClaimNextAsync(Lease);
        _time.Advance(Lease);

        var second = await _outbox.ClaimNextAsync(Lease);

        Assert.NotNull(second);
        Assert.Equal(first!.Id, second!.Id);
        Assert.Equal(2, second.AttemptCount);
    }

    [Fact]
    public async Task MarkSent_CompletesTheMessageAndDropsItsPayload()
    {
        await _outbox.EnqueueAsync(Submission());
        var claimed = await _outbox.ClaimNextAsync(Lease);
        _time.Advance(TimeSpan.FromSeconds(3));

        await _outbox.MarkSentAsync(claimed!);

        var stored = await StoredAsync(claimed!.Id);
        Assert.Equal(DeliveryStatus.Sent, stored.Status);
        Assert.Equal(_time.GetUtcNow(), stored.CompletedAt);
        Assert.Null(stored.Error);
        Assert.Null(stored.Payload);
        Assert.Null(stored.NextAttemptAt);
        Assert.Null(await _outbox.ClaimNextAsync(Lease));
    }

    [Fact]
    public async Task MarkFailed_CompletesTheMessageWithItsErrorAndDropsItsPayload()
    {
        await _outbox.EnqueueAsync(Submission());
        var claimed = await _outbox.ClaimNextAsync(Lease);

        await _outbox.MarkFailedAsync(claimed!, "the destination was rejected");

        var stored = await StoredAsync(claimed!.Id);
        Assert.Equal(DeliveryStatus.Failed, stored.Status);
        Assert.Equal("the destination was rejected", stored.Error);
        Assert.NotNull(stored.CompletedAt);
        Assert.Null(stored.Payload);
        Assert.Null(await _outbox.ClaimNextAsync(Lease));
    }

    [Fact]
    public async Task ScheduleRetry_MakesTheMessageClaimableOnlyAfterTheDelay()
    {
        await _outbox.EnqueueAsync(Submission(payload: "kept for the retry"));
        var claimed = await _outbox.ClaimNextAsync(Lease);

        await _outbox.ScheduleRetryAsync(claimed!, "temporarily unreachable", TimeSpan.FromSeconds(30));

        var stored = await StoredAsync(claimed!.Id);
        Assert.Equal(DeliveryStatus.Queued, stored.Status);
        Assert.Equal("temporarily unreachable", stored.Error);
        Assert.Null(stored.CompletedAt);

        _time.Advance(TimeSpan.FromSeconds(29));
        Assert.Null(await _outbox.ClaimNextAsync(Lease));

        _time.Advance(TimeSpan.FromSeconds(1));
        var retried = await _outbox.ClaimNextAsync(Lease);
        Assert.Equal(claimed.Id, retried!.Id);
        Assert.Equal("kept for the retry", retried.Payload);
        Assert.Equal(2, retried.AttemptCount);
    }

    [Fact]
    public async Task Release_ReturnsTheMessageToTheQueueWithoutCountingTheAttempt()
    {
        await _outbox.EnqueueAsync(Submission());
        var claimed = await _outbox.ClaimNextAsync(Lease);

        await _outbox.ReleaseAsync(claimed!);

        var stored = await StoredAsync(claimed!.Id);
        Assert.Equal(DeliveryStatus.Queued, stored.Status);
        Assert.Equal(0, stored.AttemptCount);

        var reclaimed = await _outbox.ClaimNextAsync(Lease);
        Assert.Equal(claimed.Id, reclaimed!.Id);
        Assert.Equal(1, reclaimed.AttemptCount);
    }

    [Fact]
    public async Task OutcomeFromAnAttemptWhoseLeaseWasTakenOver_IsIgnored()
    {
        await _outbox.EnqueueAsync(Submission());
        var stale = await _outbox.ClaimNextAsync(Lease);
        _time.Advance(Lease);
        var current = await _outbox.ClaimNextAsync(Lease);

        await _outbox.MarkFailedAsync(stale!, "reported too late");

        var stored = await StoredAsync(current!.Id);
        Assert.Equal(DeliveryStatus.Sending, stored.Status);
        Assert.Null(stored.Error);
        Assert.Equal(2, stored.AttemptCount);
    }
}
