using Microsoft.AspNetCore.DataProtection;
using Sendway.Core;
using Xunit;

namespace Sendway.Core.Tests;

// The order due messages are delivered in: earliest deadline first, a message without a deadline counted as
// due a day after it was accepted — so a verification code overtakes a bulk send, the bulk send stays first
// come, first served among itself, and nothing waits longer than a day for a newer message with a deadline.
public class ServeOrderTests : IAsyncLifetime
{
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(1);

    private readonly TestDatabase _database = new();
    private readonly ManualTimeProvider _time = new();
    private readonly Guid _tenantId = Guid.NewGuid();
    private MessageOutbox _outbox = null!;

    public async Task InitializeAsync()
    {
        await _database.CreateAsync();
        await _database.AllowEveryChannelAsync();
        _outbox = new MessageOutbox(_database.Factory, new EphemeralDataProtectionProvider(), _time, new WebhookDeliverySignal());
    }

    public Task DisposeAsync()
    {
        _database.Dispose();
        return Task.CompletedTask;
    }

    private async Task<Guid> AcceptAsync(TimeSpan? expiresIn = null)
    {
        var expiresAt = expiresIn is { } span ? _time.GetUtcNow() + span : (DateTimeOffset?)null;
        var result = await _outbox.EnqueueAsync(new MessageSubmission(_tenantId, MessageChannels.Email, "user@example.com", "payload", ExpiresAt: expiresAt));
        return result.Record!.Id;
    }

    private async Task<Guid> NextAsync() => (await _outbox.ClaimNextAsync(Lease))!.Id;

    [Fact]
    public async Task AMessageWithANearDeadline_IsDeliveredBeforeEarlierMessagesWithout()
    {
        var bulk1 = await AcceptAsync();
        _time.Advance(TimeSpan.FromSeconds(1));
        var bulk2 = await AcceptAsync();
        _time.Advance(TimeSpan.FromSeconds(1));
        var code = await AcceptAsync(TimeSpan.FromMinutes(10));

        Assert.Equal(code, await NextAsync());
        Assert.Equal(bulk1, await NextAsync());
        Assert.Equal(bulk2, await NextAsync());
    }

    [Fact]
    public async Task MessagesWithoutADeadline_KeepFirstComeFirstServedOrder()
    {
        var ids = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            ids.Add(await AcceptAsync());
            _time.Advance(TimeSpan.FromSeconds(1));
        }

        var served = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            served.Add(await NextAsync());
        }

        Assert.Equal(ids, served);
    }

    [Fact]
    public async Task TheEarlierDeadlineGoesFirst()
    {
        var later = await AcceptAsync(TimeSpan.FromHours(2));
        var sooner = await AcceptAsync(TimeSpan.FromMinutes(5));

        Assert.Equal(sooner, await NextAsync());
        Assert.Equal(later, await NextAsync());
    }

    [Fact]
    public async Task ADeadlineBeyondADay_DoesNotMoveAMessageAheadOrBehind()
    {
        var first = await AcceptAsync();
        _time.Advance(TimeSpan.FromSeconds(1));
        var distant = await AcceptAsync(TimeSpan.FromDays(7));
        _time.Advance(TimeSpan.FromSeconds(1));
        var third = await AcceptAsync();

        Assert.Equal(first, await NextAsync());
        Assert.Equal(distant, await NextAsync());
        Assert.Equal(third, await NextAsync());
    }

    [Fact]
    public async Task AMessageThatHasWaitedADay_IsNotOvertakenByNewerOnesWithDeadlines()
    {
        var waiting = await AcceptAsync();
        _time.Advance(MessageOutbox.ServeWindow - TimeSpan.FromMinutes(5));

        // Due a day after it was accepted, which is five minutes from now: before a code good for ten.
        var code = await AcceptAsync(TimeSpan.FromMinutes(10));

        Assert.Equal(waiting, await NextAsync());
        Assert.Equal(code, await NextAsync());
    }

    [Fact]
    public async Task ASimulatedMessage_NeverOvertakesARealOne_WhateverItsDeadline()
    {
        var real = await AcceptAsync();
        _time.Advance(TimeSpan.FromSeconds(1));
        var simulated = (await _outbox.EnqueueAsync(new MessageSubmission(
            _tenantId, MessageChannels.Email, "user@example.com", "payload", ExpiresAt: _time.GetUtcNow() + TimeSpan.FromMinutes(1), Simulated: true))).Record!.Id;

        Assert.Equal(real, await NextAsync());
        Assert.Equal(simulated, await NextAsync());
    }

    [Fact]
    public async Task ARetryKeepsItsPlaceInLine()
    {
        var code = await AcceptAsync(TimeSpan.FromMinutes(10));
        var bulk = await AcceptAsync();

        var claim = (await _outbox.ClaimNextAsync(Lease))!;
        Assert.Equal(code, claim.Id);
        await _outbox.ScheduleRetryAsync(claim, "temporary", TimeSpan.FromSeconds(30));
        _time.Advance(TimeSpan.FromSeconds(31));

        // The retry is due again; it is still the more urgent of the two.
        Assert.Equal(code, await NextAsync());
        Assert.Equal(bulk, await NextAsync());
    }

    [Fact]
    public void TheWindowIsADay()
    {
        Assert.Equal(TimeSpan.FromHours(24), MessageOutbox.ServeWindow);
    }
}
