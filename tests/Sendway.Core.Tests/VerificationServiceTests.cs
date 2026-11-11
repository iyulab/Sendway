using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Sendway.Core;
using Xunit;

namespace Sendway.Core.Tests;

// Proving that the person holds the address or number: a code Sendway makes, sends through the ordinary
// message path and checks. These tests read the code from the start result, which only a sandbox tenant gets
// (nobody could otherwise read it), except where the delivered message itself is the subject.
public class VerificationServiceTests : IAsyncLifetime
{
    private readonly TestDatabase _database = new();
    private readonly ManualTimeProvider _time = new();
    private readonly EphemeralDataProtectionProvider _protection = new();
    private readonly Guid _tenantId = Guid.NewGuid();
    private MessageOutbox _outbox = null!;
    private VerificationService _service = null!;

    public async Task InitializeAsync()
    {
        await _database.CreateAsync();
        await _database.AllowEveryChannelAsync();
        _outbox = new MessageOutbox(_database.Factory, _protection, _time, new WebhookDeliverySignal());
        _service = new VerificationService(_database.Factory, _outbox, _protection, _time, Options.Create(new VerificationOptions()), new MessageDeliverySignal());
    }

    public Task DisposeAsync()
    {
        _database.Dispose();
        return Task.CompletedTask;
    }

    private VerificationStart Start(
        string to = "user@example.com",
        string channel = MessageChannels.Email,
        string purpose = "signup",
        int maxAttempts = 3,
        int codeLength = 6,
        string locale = "en",
        string? key = null,
        bool simulated = true,
        Guid? tenantId = null,
        int? destinationPerHour = null,
        int? perDay = null,
        TimeSpan? ttl = null) =>
        new(tenantId ?? _tenantId, channel, to, purpose, ttl ?? TimeSpan.FromMinutes(5), codeLength, maxAttempts, locale, key, simulated, perDay, destinationPerHour, null);

    private async Task<(Verification Verification, string Code)> StartedAsync(VerificationStart request)
    {
        var result = await _service.StartAsync(request);
        Assert.Equal(VerificationStartOutcome.Started, result.Outcome);
        return (result.Verification!, result.Code!);
    }

    private async Task<int> MessageCountAsync()
    {
        await using var db = await _database.Factory.CreateDbContextAsync();
        return await db.MessageRecords.CountAsync();
    }

    private async Task<Verification> RowAsync(Guid id)
    {
        await using var db = await _database.Factory.CreateDbContextAsync();
        return await db.Verifications.AsNoTracking().SingleAsync(v => v.Id == id);
    }

    [Fact]
    public async Task Start_MakesACodeOfTheRequestedLength_AndQueuesItsMessageWithTheSameDeadline()
    {
        var (verification, code) = await StartedAsync(Start(codeLength: 8));

        Assert.Equal(8, code.Length);
        Assert.All(code, c => Assert.True(char.IsAsciiDigit(c)));
        Assert.Equal(VerificationStatus.Pending, verification.Status);
        Assert.Equal(_time.GetUtcNow() + TimeSpan.FromMinutes(5), verification.ExpiresAt);

        var message = await _outbox.GetAsync(verification.MessageId!.Value);
        Assert.Equal(verification.ExpiresAt, message!.ExpiresAt);
        Assert.Equal(DeliveryStatus.Queued, message.Status);
    }

    [Fact]
    public async Task ARealTenant_NeverGetsTheCodeBack_ButTheDeliveredMessageCarriesIt()
    {
        var result = await _service.StartAsync(Start(simulated: false));

        Assert.Equal(VerificationStartOutcome.Started, result.Outcome);
        Assert.Null(result.Code);
        var claim = (await _outbox.ClaimNextAsync(TimeSpan.FromMinutes(1)))!;
        var payload = JsonDocument.Parse(_outbox.ReadPayload(claim)).RootElement;
        var body = payload.GetProperty("Body").GetString()!;
        Assert.Matches(@"^Your verification code is \d{6}\. It expires in 5 minutes\.$", body);

        // ...and it is the code that checks out.
        var code = body.Split(' ')[4].TrimEnd('.');
        Assert.Equal(VerificationCheckOutcome.Verified, (await _service.CheckAsync(_tenantId, result.Verification!.Id, code)).Outcome);
    }

    [Fact]
    public async Task TheKoreanWording_IsFixedToo()
    {
        await _service.StartAsync(Start(locale: "ko", simulated: false));
        var claim = (await _outbox.ClaimNextAsync(TimeSpan.FromMinutes(1)))!;

        var payload = JsonDocument.Parse(_outbox.ReadPayload(claim)).RootElement;
        Assert.Equal("인증번호 안내", payload.GetProperty("Subject").GetString());
        Assert.Matches(@"^인증번호는 \d{6}입니다\. 5분 안에 입력해 주세요\.$", payload.GetProperty("Body").GetString()!);
    }

    [Fact]
    public async Task ARightCode_Verifies_Once()
    {
        var (verification, code) = await StartedAsync(Start());

        Assert.Equal(VerificationCheckOutcome.Verified, (await _service.CheckAsync(_tenantId, verification.Id, code)).Outcome);
        Assert.Equal(VerificationCheckOutcome.Expired, (await _service.CheckAsync(_tenantId, verification.Id, code)).Outcome);
        Assert.Null((await RowAsync(verification.Id)).ProtectedCode);
    }

    [Fact]
    public async Task AWrongCode_CountsDown_ThenTheCodeIsDeadEvenWhenRight()
    {
        var (verification, code) = await StartedAsync(Start(maxAttempts: 3));
        var wrong = code == "000000" ? "111111" : "000000";

        var first = await _service.CheckAsync(_tenantId, verification.Id, wrong);
        var second = await _service.CheckAsync(_tenantId, verification.Id, wrong);
        var third = await _service.CheckAsync(_tenantId, verification.Id, wrong);

        Assert.Equal((VerificationCheckOutcome.Mismatch, 2), (first.Outcome, first.AttemptsLeft));
        Assert.Equal((VerificationCheckOutcome.Mismatch, 1), (second.Outcome, second.AttemptsLeft));
        Assert.Equal((VerificationCheckOutcome.Mismatch, 0), (third.Outcome, third.AttemptsLeft));
        Assert.Equal(VerificationCheckOutcome.TooManyAttempts, (await _service.CheckAsync(_tenantId, verification.Id, code)).Outcome);
        Assert.Null((await RowAsync(verification.Id)).ProtectedCode);
    }

    [Fact]
    public async Task ParallelGuesses_CannotGetMoreThanTheAttemptsAllowed()
    {
        var (verification, code) = await StartedAsync(Start(maxAttempts: 3));
        var wrong = code == "000000" ? "111111" : "000000";

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => _service.CheckAsync(_tenantId, verification.Id, wrong)));

        Assert.Equal(3, results.Count(r => r.Outcome == VerificationCheckOutcome.Mismatch));
        Assert.Equal(17, results.Count(r => r.Outcome == VerificationCheckOutcome.TooManyAttempts));
        Assert.Equal(3, (await RowAsync(verification.Id)).Attempts);
    }

    [Fact]
    public async Task ParallelRightCodes_VerifyExactlyOnce()
    {
        var (verification, code) = await StartedAsync(Start(maxAttempts: 10));

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => _service.CheckAsync(_tenantId, verification.Id, code)));

        Assert.Equal(1, results.Count(r => r.Outcome == VerificationCheckOutcome.Verified));
    }

    [Fact]
    public async Task AfterItsTime_TheCodeIsExpired()
    {
        var (verification, code) = await StartedAsync(Start(ttl: TimeSpan.FromMinutes(2)));
        _time.Advance(TimeSpan.FromMinutes(2));

        Assert.Equal(VerificationCheckOutcome.Expired, (await _service.CheckAsync(_tenantId, verification.Id, code)).Outcome);
        Assert.Equal("expired", (await _service.GetAsync(_tenantId, verification.Id))!.State);
    }

    [Fact]
    public async Task ANewCode_ReplacesTheOldOneForTheSameDestinationAndPurposeOnly()
    {
        var (first, firstCode) = await StartedAsync(Start(purpose: "signup"));
        var (otherPurpose, otherCode) = await StartedAsyncAfterInterval(Start(purpose: "withdrawal"));
        var (second, secondCode) = await StartedAsyncAfterInterval(Start(purpose: "signup"));

        Assert.Equal(VerificationCheckOutcome.Expired, (await _service.CheckAsync(_tenantId, first.Id, firstCode)).Outcome);
        Assert.Equal(VerificationCheckOutcome.Verified, (await _service.CheckAsync(_tenantId, second.Id, secondCode)).Outcome);
        Assert.Equal(VerificationCheckOutcome.Verified, (await _service.CheckAsync(_tenantId, otherPurpose.Id, otherCode)).Outcome);
        Assert.Equal("superseded", (await _service.GetAsync(_tenantId, first.Id))!.State);
    }

    [Fact]
    public async Task AStartThatIsRefused_LeavesTheWorkingCodeWorking()
    {
        var (first, firstCode) = await StartedAsync(Start(destinationPerHour: 1));
        _time.Advance(TimeSpan.FromMinutes(1));

        var refused = await _service.StartAsync(Start(destinationPerHour: 1));

        Assert.Equal(VerificationStartOutcome.DestinationLimited, refused.Outcome);
        Assert.Equal(VerificationCheckOutcome.Verified, (await _service.CheckAsync(_tenantId, first.Id, firstCode)).Outcome);
    }

    [Fact]
    public async Task ADailyLimit_RefusesTheStart_AndStoresNothing()
    {
        var first = await _service.StartAsync(Start(to: "a@example.com", perDay: 1));
        var second = await _service.StartAsync(Start(to: "b@example.com", perDay: 1));

        Assert.Equal(VerificationStartOutcome.Started, first.Outcome);
        Assert.Equal(VerificationStartOutcome.DailyLimitReached, second.Outcome);
        await using var db = await _database.Factory.CreateDbContextAsync();
        Assert.Equal(1, await db.Verifications.CountAsync());
    }

    [Fact]
    public async Task AskingAgainTooSoon_IsRefusedWithWhenToTryAgain_AndAnotherDestinationIsNot()
    {
        await StartedAsync(Start());
        _time.Advance(TimeSpan.FromSeconds(10));

        var tooSoon = await _service.StartAsync(Start(to: "User <USER@example.com>"));

        Assert.Equal(VerificationStartOutcome.ResendTooSoon, tooSoon.Outcome);
        Assert.Equal(TimeSpan.FromSeconds(20), tooSoon.RetryAfter);
        Assert.Equal(VerificationStartOutcome.Started, (await _service.StartAsync(Start(to: "other@example.com"))).Outcome);

        _time.Advance(TimeSpan.FromSeconds(20));
        Assert.Equal(VerificationStartOutcome.Started, (await _service.StartAsync(Start())).Outcome);
    }

    [Fact]
    public async Task AnOrdinaryMessageJustBefore_DoesNotCountAsAResend()
    {
        // A sign-up mail and, seconds later, the code: the resend interval counts codes only.
        await _outbox.EnqueueAsync(new MessageSubmission(_tenantId, MessageChannels.Email, "user@example.com", "welcome"));

        Assert.Equal(VerificationStartOutcome.Started, (await _service.StartAsync(Start())).Outcome);
    }

    [Fact]
    public async Task ParallelStarts_ForOneDestination_AreOnlyOneCode()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => _service.StartAsync(Start())));

        Assert.Equal(1, results.Count(r => r.Outcome == VerificationStartOutcome.Started));
        Assert.Equal(9, results.Count(r => r.Outcome == VerificationStartOutcome.ResendTooSoon));
        Assert.Equal(1, await MessageCountAsync());
    }

    [Fact]
    public async Task ARepeatedKey_IsTheSameVerification_NotASecondCodeOrMessage()
    {
        var first = await _service.StartAsync(Start(key: "k-1"));
        var replay = await _service.StartAsync(Start(key: "k-1"));
        var different = await _service.StartAsync(Start(key: "k-1", codeLength: 8));

        Assert.Equal(VerificationStartOutcome.Started, first.Outcome);
        Assert.Equal(VerificationStartOutcome.Replayed, replay.Outcome);
        Assert.Equal(first.Verification!.Id, replay.Verification!.Id);
        Assert.Equal(first.Code, replay.Code);
        Assert.Equal(VerificationStartOutcome.KeyReused, different.Outcome);
        Assert.Equal(1, await MessageCountAsync());
    }

    [Fact]
    public async Task AnotherTenant_CannotCheckOrReadAVerification()
    {
        var (verification, code) = await StartedAsync(Start());
        var stranger = Guid.NewGuid();

        Assert.Equal(VerificationCheckOutcome.NotFound, (await _service.CheckAsync(stranger, verification.Id, code)).Outcome);
        Assert.Null(await _service.GetAsync(stranger, verification.Id));
        Assert.Equal(VerificationCheckOutcome.NotFound, (await _service.CheckAsync(_tenantId, Guid.NewGuid(), code)).Outcome);
        Assert.Equal(0, (await RowAsync(verification.Id)).Attempts);
    }

    [Fact]
    public async Task ASmsGoesToTheNumber_AndAMalformedDestinationIsRefused()
    {
        var sms = await _service.StartAsync(Start(to: "+821012345678", channel: MessageChannels.Sms));
        Assert.Equal(VerificationStartOutcome.Started, sms.Outcome);
        Assert.Equal("+821012345678", sms.Verification!.Destination);

        await Assert.ThrowsAsync<InvalidRecipientException>(() => _service.StartAsync(Start(to: "not-an-address")));
        await Assert.ThrowsAsync<InvalidRecipientException>(() => _service.StartAsync(Start(to: "01012345678", channel: MessageChannels.Sms)));
    }

    [Fact]
    public async Task TheView_ShowsTheStateAndHowTheMessageEnded()
    {
        var (verification, _) = await StartedAsync(Start(simulated: false));
        var pending = (await _service.GetAsync(_tenantId, verification.Id))!;
        Assert.Equal(("pending", "Queued", null), (pending.State, pending.DeliveryStatus, pending.DeliveryReason));

        var claim = (await _outbox.ClaimNextAsync(TimeSpan.FromMinutes(1)))!;
        await _outbox.MarkFailedAsync(claim, "no such mailbox", MessageFailureReasons.RecipientRefused);

        var failed = (await _service.GetAsync(_tenantId, verification.Id))!;
        Assert.Equal(("Failed", MessageFailureReasons.RecipientRefused), (failed.DeliveryStatus, failed.DeliveryReason));
    }

    // The next code for a destination can be asked for after the resend interval.
    private async Task<(Verification Verification, string Code)> StartedAsyncAfterInterval(VerificationStart request)
    {
        _time.Advance(TimeSpan.FromSeconds(31));
        return await StartedAsync(request);
    }
}
