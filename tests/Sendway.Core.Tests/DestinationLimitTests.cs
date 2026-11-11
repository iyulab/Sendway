using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Sendway.Core;
using Xunit;

namespace Sendway.Core.Tests;

// How often one destination may be sent to by one tenant: the cooldown between two messages and the
// per-hour count, both counted over accepted messages.
public class DestinationLimitTests : IAsyncLifetime
{
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

    private MessageSubmission To(
        string destination, int? perHour = null, TimeSpan? cooldown = null, string? key = null, Guid? tenantId = null, string channel = MessageChannels.Email) =>
        new(tenantId ?? _tenantId, channel, destination, "payload", key, Destination: destination, DestinationPerHour: perHour, DestinationCooldown: cooldown);

    [Fact]
    public async Task WithoutALimit_ThereIsNoLimit()
    {
        for (var i = 0; i < 50; i++)
        {
            Assert.Equal(EnqueueOutcome.Accepted, (await _outbox.EnqueueAsync(To("a@example.com"))).Outcome);
        }
    }

    [Fact]
    public async Task Cooldown_RefusesASecondMessageToTheSameDestination_UntilItHasPassed()
    {
        var cooldown = TimeSpan.FromSeconds(60);
        Assert.Equal(EnqueueOutcome.Accepted, (await _outbox.EnqueueAsync(To("a@example.com", cooldown: cooldown))).Outcome);

        _time.Advance(TimeSpan.FromSeconds(20));
        var refused = await _outbox.EnqueueAsync(To("a@example.com", cooldown: cooldown));

        Assert.Equal(EnqueueOutcome.DestinationLimited, refused.Outcome);
        Assert.Null(refused.Record);
        Assert.Equal(TimeSpan.FromSeconds(40), refused.RetryAfter);

        _time.Advance(TimeSpan.FromSeconds(40));
        Assert.Equal(EnqueueOutcome.Accepted, (await _outbox.EnqueueAsync(To("a@example.com", cooldown: cooldown))).Outcome);
    }

    [Fact]
    public async Task APerHourLimit_CountsTheHourBackFromNow_AndSaysWhenTheOldestLeavesIt()
    {
        await _outbox.EnqueueAsync(To("a@example.com", perHour: 2));
        _time.Advance(TimeSpan.FromMinutes(10));
        await _outbox.EnqueueAsync(To("a@example.com", perHour: 2));
        _time.Advance(TimeSpan.FromMinutes(10));

        var refused = await _outbox.EnqueueAsync(To("a@example.com", perHour: 2));

        Assert.Equal(EnqueueOutcome.DestinationLimited, refused.Outcome);
        Assert.Equal(TimeSpan.FromMinutes(40), refused.RetryAfter);

        _time.Advance(TimeSpan.FromMinutes(40));
        Assert.Equal(EnqueueOutcome.Accepted, (await _outbox.EnqueueAsync(To("a@example.com", perHour: 2))).Outcome);
    }

    [Fact]
    public async Task WhenBothApply_TheLongerWaitIsReported()
    {
        await _outbox.EnqueueAsync(To("a@example.com", perHour: 1, cooldown: TimeSpan.FromSeconds(30)));
        _time.Advance(TimeSpan.FromMinutes(5));

        var refused = await _outbox.EnqueueAsync(To("a@example.com", perHour: 1, cooldown: TimeSpan.FromSeconds(30)));

        // The cooldown has passed; the hour has not.
        Assert.Equal(TimeSpan.FromMinutes(55), refused.RetryAfter);
    }

    [Fact]
    public async Task ACooldownLongerThanAnHour_IsHonouredForItsWholeLength()
    {
        var cooldown = TimeSpan.FromHours(3);
        await _outbox.EnqueueAsync(To("a@example.com", cooldown: cooldown));
        _time.Advance(TimeSpan.FromMinutes(90));

        var refused = await _outbox.EnqueueAsync(To("a@example.com", cooldown: cooldown));

        Assert.Equal(EnqueueOutcome.DestinationLimited, refused.Outcome);
        Assert.Equal(TimeSpan.FromMinutes(90), refused.RetryAfter);
    }

    [Fact]
    public async Task APerHourLimitOfZero_RefusesEverything_AndSaysToWaitTheHour()
    {
        var refused = await _outbox.EnqueueAsync(To("a@example.com", perHour: 0));

        Assert.Equal(EnqueueOutcome.DestinationLimited, refused.Outcome);
        Assert.Equal(TenantLimits.DestinationWindow, refused.RetryAfter);
    }

    [Fact]
    public async Task OtherDestinations_OtherChannels_AndOtherTenants_AreNotAffected()
    {
        await _outbox.EnqueueAsync(To("a@example.com", perHour: 1));

        Assert.Equal(EnqueueOutcome.DestinationLimited, (await _outbox.EnqueueAsync(To("a@example.com", perHour: 1))).Outcome);
        Assert.Equal(EnqueueOutcome.Accepted, (await _outbox.EnqueueAsync(To("b@example.com", perHour: 1))).Outcome);
        Assert.Equal(EnqueueOutcome.Accepted, (await _outbox.EnqueueAsync(To("a@example.com", perHour: 1, channel: MessageChannels.Push))).Outcome);
        Assert.Equal(EnqueueOutcome.Accepted, (await _outbox.EnqueueAsync(To("a@example.com", perHour: 1, tenantId: Guid.NewGuid()))).Outcome);
    }

    [Fact]
    public async Task ARepeatedIdempotencyKey_IsAReplay_NotAnotherMessageToTheDestination()
    {
        var first = await _outbox.EnqueueAsync(To("a@example.com", perHour: 1, key: "k-1"));
        var replay = await _outbox.EnqueueAsync(To("a@example.com", perHour: 1, key: "k-1"));
        var another = await _outbox.EnqueueAsync(To("a@example.com", perHour: 1, key: "k-2"));

        Assert.Equal(EnqueueOutcome.Accepted, first.Outcome);
        Assert.Equal(EnqueueOutcome.Replayed, replay.Outcome);
        Assert.Equal(first.Record!.Id, replay.Record!.Id);
        Assert.Equal(EnqueueOutcome.DestinationLimited, another.Outcome);
    }

    [Fact]
    public async Task AMessageWithoutADestination_IsNeverCounted()
    {
        for (var i = 0; i < 3; i++)
        {
            var many = new MessageSubmission(_tenantId, MessageChannels.Email, "a@example.com, b@example.com", "payload", DestinationPerHour: 1);
            Assert.Equal(EnqueueOutcome.Accepted, (await _outbox.EnqueueAsync(many)).Outcome);
        }
    }

    [Fact]
    public async Task AFailedMessage_StillCounts()
    {
        var first = await _outbox.EnqueueAsync(To("a@example.com", perHour: 1));
        var claim = (await _outbox.ClaimNextAsync(TimeSpan.FromMinutes(1)))!;
        await _outbox.MarkFailedAsync(claim, "refused for good");

        Assert.Equal(EnqueueOutcome.Accepted, first.Outcome);
        Assert.Equal(EnqueueOutcome.DestinationLimited, (await _outbox.EnqueueAsync(To("a@example.com", perHour: 1))).Outcome);
    }

    [Theory]
    [InlineData("a@example.com", "A@EXAMPLE.COM")]
    [InlineData("Name <a@example.com>", "a@example.com")]
    [InlineData("user@bücher.example", "user@xn--bcher-kva.example")]
    public void AnEmailDestinationKey_IsTheMailboxNotTheSpelling(string one, string other)
    {
        Assert.Equal(EmailDestination.Key(one), EmailDestination.Key(other));
    }

    [Fact]
    public async Task ParallelRequests_CannotGetPastThePerHourLimit_EvenWhenSavingIsSlow()
    {
        // A slow save keeps every request between its count and its insert for a while, so without the
        // per-destination gate they would all be counted against an empty hour.
        var slow = new MessageOutbox(_database.FactoryWith(new SlowSave(TimeSpan.FromMilliseconds(150))), new EphemeralDataProtectionProvider(), _time, new WebhookDeliverySignal());

        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => slow.EnqueueAsync(To("a@example.com", perHour: 3))));

        Assert.Equal(3, results.Count(r => r.Outcome == EnqueueOutcome.Accepted));
        Assert.Equal(9, results.Count(r => r.Outcome == EnqueueOutcome.DestinationLimited));
    }

    private sealed class SlowSave(TimeSpan delay) : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
    {
        public override async ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            await Task.Delay(delay, cancellationToken);
            return result;
        }
    }

    [Fact]
    public async Task ADestinationTooLongForTheColumn_IsLimitedByItsHash_NotSkipped()
    {
        var longToken = new string('t', 2000);
        var otherLongToken = new string('t', 1999) + "u";

        Assert.Equal(EnqueueOutcome.Accepted, (await _outbox.EnqueueAsync(To(longToken, perHour: 1, channel: MessageChannels.Push))).Outcome);

        Assert.Equal(EnqueueOutcome.DestinationLimited, (await _outbox.EnqueueAsync(To(longToken, perHour: 1, channel: MessageChannels.Push))).Outcome);
        Assert.Equal(EnqueueOutcome.Accepted, (await _outbox.EnqueueAsync(To(otherLongToken, perHour: 1, channel: MessageChannels.Push))).Outcome);
    }

    [Fact]
    public async Task WhenTheTenantIsOverItsDailyLimit_ThatIsWhatIsReported_NotTheDestination()
    {
        MessageSubmission Both() =>
            new(_tenantId, MessageChannels.Email, "a@example.com", "payload", PerDay: 1, Destination: "a@example.com", DestinationPerHour: 1);

        Assert.Equal(EnqueueOutcome.Accepted, (await _outbox.EnqueueAsync(Both())).Outcome);

        Assert.Equal(EnqueueOutcome.DailyLimitReached, (await _outbox.EnqueueAsync(Both())).Outcome);
    }
}
