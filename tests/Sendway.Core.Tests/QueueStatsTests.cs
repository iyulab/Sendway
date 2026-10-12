using Microsoft.AspNetCore.DataProtection;
using Sendway.Core;
using Xunit;

namespace Sendway.Core.Tests;

public class QueueStatsTests : IAsyncLifetime
{
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(1);

    private readonly TestDatabase _database = new();
    private readonly ManualTimeProvider _time = new();
    private readonly Guid _tenantId = Guid.NewGuid();
    private MessageOutbox _messages = null!;
    private WebhookOutbox _webhooks = null!;
    private WebhookEndpointStore _endpoints = null!;

    public async Task InitializeAsync()
    {
        await _database.CreateAsync();

        var dataProtection = new EphemeralDataProtectionProvider();
        _messages = new MessageOutbox(_database.Factory, dataProtection, _time, new WebhookDeliverySignal());
        _webhooks = new WebhookOutbox(_database.Factory, _time);
        _endpoints = new WebhookEndpointStore(_database.Factory, dataProtection, _time);
    }

    public Task DisposeAsync()
    {
        _database.Dispose();
        return Task.CompletedTask;
    }

    private Task EnqueueAsync() =>
        _messages.EnqueueAsync(new MessageSubmission(_tenantId, MessageChannels.Email, "user@example.com", "payload"));

    [Fact]
    public async Task EmptyQueue_ReportsNothingWaiting()
    {
        var stats = await _messages.GetStatsAsync();

        Assert.Equal(new QueueStats(Queued: 0, Sending: 0, FailedInLast24Hours: 0, OldestDueAge: null), stats);
    }

    [Fact]
    public async Task CountsItemsByWhereTheyAreInTheirLifecycle()
    {
        await EnqueueAsync();
        await EnqueueAsync();
        await EnqueueAsync();
        await EnqueueAsync();
        await _messages.MarkSentAsync((await _messages.ClaimNextAsync(Lease))!);
        await _messages.MarkFailedAsync((await _messages.ClaimNextAsync(Lease))!, "rejected");
        await _messages.ClaimNextAsync(Lease);

        var stats = await _messages.GetStatsAsync();

        Assert.Equal(1, stats.Queued);
        Assert.Equal(1, stats.Sending);
        Assert.Equal(1, stats.FailedInLast24Hours);
    }

    [Fact]
    public async Task OldestDueAge_GrowsWhileNothingPicksTheItemUp()
    {
        await EnqueueAsync();
        _time.Advance(TimeSpan.FromSeconds(90));
        await EnqueueAsync();

        var stats = await _messages.GetStatsAsync();

        Assert.Equal(TimeSpan.FromSeconds(90), stats.OldestDueAge);
    }

    [Fact]
    public async Task OldestDueAge_IgnoresItemsThatAreNotYetDue()
    {
        await EnqueueAsync();
        var claimed = await _messages.ClaimNextAsync(Lease);
        await _messages.ScheduleRetryAsync(claimed!, "temporarily unreachable", TimeSpan.FromMinutes(3));

        var waiting = await _messages.GetStatsAsync();
        Assert.Equal(1, waiting.Queued);
        Assert.Null(waiting.OldestDueAge);

        _time.Advance(TimeSpan.FromMinutes(4));
        Assert.Equal(TimeSpan.FromMinutes(1), (await _messages.GetStatsAsync()).OldestDueAge);
    }

    [Fact]
    public async Task OldestDueAge_CountsAnAttemptWhoseLeaseRanOut()
    {
        await EnqueueAsync();
        await _messages.ClaimNextAsync(Lease);

        Assert.Null((await _messages.GetStatsAsync()).OldestDueAge);

        _time.Advance(Lease + TimeSpan.FromSeconds(30));
        Assert.Equal(TimeSpan.FromSeconds(30), (await _messages.GetStatsAsync()).OldestDueAge);
    }

    [Fact]
    public async Task FailedInLast24Hours_ForgetsOlderFailures()
    {
        await EnqueueAsync();
        await _messages.MarkFailedAsync((await _messages.ClaimNextAsync(Lease))!, "rejected");

        _time.Advance(TimeSpan.FromHours(25));

        Assert.Equal(0, (await _messages.GetStatsAsync()).FailedInLast24Hours);
    }

    [Fact]
    public async Task WebhookQueue_ReportsItsOwnItems()
    {
        await _endpoints.SetAsync(_tenantId, new Uri("https://receiver.example.com/hooks"));
        await EnqueueAsync();
        await _messages.MarkSentAsync((await _messages.ClaimNextAsync(Lease))!);
        _time.Advance(TimeSpan.FromSeconds(10));

        var stats = await _webhooks.GetStatsAsync();

        Assert.Equal(1, stats.Queued);
        Assert.Equal(TimeSpan.FromSeconds(10), stats.OldestDueAge);
        Assert.Equal(0, (await _messages.GetStatsAsync()).Queued);
    }
}
