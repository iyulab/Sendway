using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Sendway.Core;

public sealed class VerificationOptions
{
    // The least time between two codes sent to one destination by one tenant — a code that does not arrive
    // is asked for again, but not every second. It counts verification starts only: a sign-up mail followed
    // by a code is not "too soon".
    public TimeSpan ResendInterval { get; set; } = TimeSpan.FromSeconds(30);
}

// The bounds on what a caller may ask for. The chance that one destination's code is guessed in an hour is
// about (starts per hour × attempts per code) / 10^length: with SMS at 5 starts an hour (the per-destination
// default) and 5 attempts, 6 digits give 25 in a million and 4 digits 25 in ten thousand — so 6 is the
// default and a shorter code is the caller's choice to make.
public static class VerificationLimits
{
    public static readonly TimeSpan MinTtl = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MaxTtl = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(5);

    public const int MinCodeLength = 4;
    public const int MaxCodeLength = 8;
    public const int DefaultCodeLength = 6;

    public const int MinAttempts = 1;
    public const int MaxAttemptsAllowed = 10;
    public const int DefaultAttempts = 5;

    public const int MaxPurposeLength = 64;

    public static readonly IReadOnlyList<string> Locales = ["en", "ko"];
}

public sealed record VerificationStart(
    Guid TenantId,
    string Channel,
    string To,
    string Purpose,
    TimeSpan Ttl,
    int CodeLength,
    int MaxAttempts,
    string Locale,
    string? IdempotencyKey,
    bool Simulated,
    int? PerDay,
    int? DestinationPerHour,
    TimeSpan? DestinationCooldown);

public enum VerificationStartOutcome
{
    Started,

    // The Idempotency-Key was already used for this same request; Verification is the original.
    Replayed,

    // The Idempotency-Key was already used for a different request.
    KeyReused,

    ChannelNotConfigured,

    DailyLimitReached,

    // The tenant's per-destination limit for the message that carries the code (hourly count or cooldown).
    DestinationLimited,

    // A code was started for this destination a moment ago; RetryAfter is when the next may be.
    ResendTooSoon
}

// Code: set only for a tenant in sandbox, whose messages are not delivered — otherwise nobody could ever
// read the code and a check could never succeed. A real tenant never gets the code back.
public sealed record VerificationStartResult(
    VerificationStartOutcome Outcome, Verification? Verification = null, string? Code = null, TimeSpan? RetryAfter = null);

public enum VerificationCheckOutcome
{
    Verified,

    // Wrong code; AttemptsLeft more tries remain.
    Mismatch,

    // Out of time, already used, or replaced by a newer code.
    Expired,

    // The tries are used up; the code no longer works whatever is entered.
    TooManyAttempts,

    NotFound
}

public sealed record VerificationCheckResult(VerificationCheckOutcome Outcome, int AttemptsLeft = 0);

// What a caller can read about a verification: where it stands, and how the message carrying the code ended.
public sealed record VerificationView(Verification Verification, string State, string? DeliveryStatus, string? DeliveryReason);

public sealed class VerificationService
{
    private const string ProtectorPurpose = "Sendway.Verification.Code";

    // Starting a code for one destination checks "was one started a moment ago" and then inserts: one at a time
    // per destination in this process, so two parallel starts cannot both pass. (Several instances can still
    // let a few through together; the per-destination message limit and the tenant's request limit bound that.)
    private static readonly SemaphoreSlim[] Gates = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    private readonly IDbContextFactory<SendwayDbContext> _dbContextFactory;
    private readonly IMessageOutbox _outbox;
    private readonly IDataProtector _protector;
    private readonly TimeProvider _timeProvider;
    private readonly VerificationOptions _options;
    private readonly MessageDeliverySignal _signal;

    public VerificationService(
        IDbContextFactory<SendwayDbContext> dbContextFactory,
        IMessageOutbox outbox,
        IDataProtectionProvider dataProtectionProvider,
        TimeProvider timeProvider,
        IOptions<VerificationOptions> options,
        MessageDeliverySignal signal)
    {
        _dbContextFactory = dbContextFactory;
        _outbox = outbox;
        _protector = dataProtectionProvider.CreateProtector(ProtectorPurpose);
        _timeProvider = timeProvider;
        _options = options.Value;
        _signal = signal;
    }

    // Throws InvalidRecipientException for an address or number that is not valid.
    public async Task<VerificationStartResult> StartAsync(VerificationStart request, CancellationToken cancellationToken = default)
    {
        var destination = MessageSubmission.DestinationKey(DestinationFor(request.Channel, request.To));
        var requestHash = RequestHash(request, destination);

        var gate = Gates[(int)((uint)HashCode.Combine(request.TenantId, request.Channel, destination) % Gates.Length)];
        await gate.WaitAsync(cancellationToken);
        try
        {
            return await StartGuardedAsync(request, destination, requestHash, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<VerificationStartResult> StartGuardedAsync(
        VerificationStart request, string destination, string requestHash, CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var now = _timeProvider.GetUtcNow();

        // Before anything is generated or sent: a retry of a start the caller already made is the same
        // verification, not a second code.
        if (request.IdempotencyKey is not null &&
            await ReplayAsync(db, request, requestHash, cancellationToken) is { } replay)
        {
            return replay;
        }

        if (_options.ResendInterval > TimeSpan.Zero)
        {
            var since = now - _options.ResendInterval;
            var latest = await db.Verifications
                .Where(v => v.TenantId == request.TenantId && v.Channel == request.Channel && v.Destination == destination && v.CreatedAt > since)
                .MaxAsync(v => (DateTimeOffset?)v.CreatedAt, cancellationToken);
            if (latest is { } last)
            {
                return new VerificationStartResult(VerificationStartOutcome.ResendTooSoon, RetryAfter: last + _options.ResendInterval - now);
            }
        }

        var code = NewCode(request.CodeLength);
        var verification = new Verification
        {
            Id = Guid.NewGuid(),
            TenantId = request.TenantId,
            Channel = request.Channel,
            Destination = destination,
            Purpose = request.Purpose,
            Status = VerificationStatus.Pending,
            ProtectedCode = _protector.Protect(code),
            MaxAttempts = request.MaxAttempts,
            CreatedAt = now,
            ExpiresAt = now + request.Ttl,
            IdempotencyKey = request.IdempotencyKey,
            RequestHash = requestHash
        };

        var submission = new MessageSubmission(
            request.TenantId,
            request.Channel,
            destination,
            BuildPayload(request, code),
            IdempotencyKey: null,
            ExpiresAt: verification.ExpiresAt,
            Simulated: request.Simulated,
            PerDay: request.PerDay,
            Destination: destination,
            DestinationPerHour: request.DestinationPerHour,
            DestinationCooldown: request.DestinationCooldown)
        {
            // In the same save as the message: if the message is refused nothing changes, and the code it carries
            // replaces the older one only if it was actually accepted.
            AlsoStore = async (messageDb, record, token) =>
            {
                verification.MessageId = record.Id;
                var older = await messageDb.Verifications
                    .Where(v => v.TenantId == request.TenantId && v.Channel == request.Channel && v.Destination == destination
                        && v.Purpose == request.Purpose && v.Status == VerificationStatus.Pending)
                    .ToListAsync(token);
                foreach (var previous in older)
                {
                    previous.Status = VerificationStatus.Superseded;
                    previous.ProtectedCode = null;
                }

                messageDb.Verifications.Add(verification);
            }
        };

        EnqueueResult result;
        try
        {
            result = await _outbox.EnqueueAsync(submission, cancellationToken);
        }
        catch (DbUpdateException) when (request.IdempotencyKey is not null)
        {
            // The unique (tenant, key) index: a parallel start with the same key won. Answer with that one.
            await using var again = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
            return await ReplayAsync(again, request, requestHash, cancellationToken) ?? throw new InvalidOperationException("Verification could not be stored.");
        }

        switch (result.Outcome)
        {
            case EnqueueOutcome.Accepted:
                _signal.Notify();
                return new VerificationStartResult(VerificationStartOutcome.Started, verification, request.Simulated ? code : null);
            case EnqueueOutcome.ChannelNotConfigured:
                return new VerificationStartResult(VerificationStartOutcome.ChannelNotConfigured);
            case EnqueueOutcome.DailyLimitReached:
                return new VerificationStartResult(VerificationStartOutcome.DailyLimitReached, RetryAfter: result.RetryAfter);
            case EnqueueOutcome.DestinationLimited:
                return new VerificationStartResult(VerificationStartOutcome.DestinationLimited, RetryAfter: result.RetryAfter);
            default:
                throw new InvalidOperationException($"Unexpected outcome {result.Outcome} for a verification message.");
        }
    }

    private async Task<VerificationStartResult?> ReplayAsync(
        SendwayDbContext db, VerificationStart request, string requestHash, CancellationToken cancellationToken)
    {
        var existing = await db.Verifications.AsNoTracking()
            .FirstOrDefaultAsync(v => v.TenantId == request.TenantId && v.IdempotencyKey == request.IdempotencyKey, cancellationToken);
        if (existing is null)
        {
            return null;
        }

        if (existing.RequestHash != requestHash)
        {
            return new VerificationStartResult(VerificationStartOutcome.KeyReused);
        }

        var code = request.Simulated && existing.ProtectedCode is { } protectedCode ? TryUnprotect(protectedCode) : null;
        return new VerificationStartResult(VerificationStartOutcome.Replayed, existing, code);
    }

    // Counts the try first, atomically, and only then compares: parallel guesses each use up an attempt, so
    // none gets more than MaxAttempts tries.
    public async Task<VerificationCheckResult> CheckAsync(Guid tenantId, Guid id, string code, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var now = _timeProvider.GetUtcNow();

        var counted = await db.Verifications
            .Where(v => v.Id == id && v.TenantId == tenantId && v.Status == VerificationStatus.Pending && v.Attempts < v.MaxAttempts && v.ExpiresAt > now)
            .ExecuteUpdateAsync(update => update.SetProperty(v => v.Attempts, v => v.Attempts + 1), cancellationToken);

        var row = await db.Verifications.AsNoTracking().FirstOrDefaultAsync(v => v.Id == id && v.TenantId == tenantId, cancellationToken);
        if (row is null)
        {
            return new VerificationCheckResult(VerificationCheckOutcome.NotFound);
        }

        if (counted == 0)
        {
            return row.Status != VerificationStatus.Pending || now >= row.ExpiresAt
                ? new VerificationCheckResult(VerificationCheckOutcome.Expired)
                : new VerificationCheckResult(VerificationCheckOutcome.TooManyAttempts);
        }

        var stored = row.ProtectedCode is { } protectedCode ? TryUnprotect(protectedCode) : null;
        if (stored is null)
        {
            return new VerificationCheckResult(VerificationCheckOutcome.Expired);
        }

        if (CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(stored), Encoding.UTF8.GetBytes(code.Trim())))
        {
            DateTimeOffset? verifiedAt = now;
            var won = await db.Verifications
                .Where(v => v.Id == id && v.Status == VerificationStatus.Pending)
                .ExecuteUpdateAsync(update => update
                    .SetProperty(v => v.Status, VerificationStatus.Verified)
                    .SetProperty(v => v.VerifiedAt, verifiedAt)
                    .SetProperty(v => v.ProtectedCode, (string?)null), cancellationToken);

            // Zero rows: a parallel check with the same right code got there first. A code works once.
            return won == 1
                ? new VerificationCheckResult(VerificationCheckOutcome.Verified)
                : new VerificationCheckResult(VerificationCheckOutcome.Expired);
        }

        var left = Math.Max(0, row.MaxAttempts - row.Attempts);
        if (left == 0)
        {
            await db.Verifications
                .Where(v => v.Id == id && v.Status == VerificationStatus.Pending)
                .ExecuteUpdateAsync(update => update.SetProperty(v => v.ProtectedCode, (string?)null), cancellationToken);
        }

        return new VerificationCheckResult(VerificationCheckOutcome.Mismatch, left);
    }

    public async Task<VerificationView?> GetAsync(Guid tenantId, Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var verification = await db.Verifications.AsNoTracking().FirstOrDefaultAsync(v => v.Id == id && v.TenantId == tenantId, cancellationToken);
        if (verification is null)
        {
            return null;
        }

        var message = verification.MessageId is { } messageId
            ? await db.MessageRecords.AsNoTracking().Where(m => m.Id == messageId).Select(m => new { m.Status, m.FailureReason }).FirstOrDefaultAsync(cancellationToken)
            : null;

        return new VerificationView(verification, StateOf(verification, _timeProvider.GetUtcNow()), message?.Status.ToString(), message?.FailureReason);
    }

    // What a caller is told about a verification's state.
    public static string StateOf(Verification verification, DateTimeOffset now) =>
        verification.Status switch
        {
            VerificationStatus.Verified => "verified",
            VerificationStatus.Superseded => "superseded",
            _ when now >= verification.ExpiresAt => "expired",
            _ when verification.Attempts >= verification.MaxAttempts => "too_many_attempts",
            _ => "pending"
        };

    private static string DestinationFor(string channel, string to) => channel switch
    {
        MessageChannels.Email => EmailDestination.Key(to),
        MessageChannels.Sms => PhoneNumber.Parse(to).ToString(),
        _ => throw new ArgumentOutOfRangeException(nameof(channel), channel, "A verification code goes by email or SMS.")
    };

    private static string NewCode(int length) =>
        RandomNumberGenerator.GetInt32(0, (int)Math.Pow(10, length)).ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(length, '0');

    private string? TryUnprotect(string protectedCode)
    {
        try
        {
            return _protector.Unprotect(protectedCode);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    // The wording is fixed on purpose: Sendway stores and renders no templates, and the one thing it puts into
    // a message is the code it made itself.
    private static string BuildPayload(VerificationStart request, string code)
    {
        var minutes = Math.Max(1, (int)Math.Ceiling(request.Ttl.TotalMinutes));
        var (subject, body) = request.Locale == "ko"
            ? ("인증번호 안내", $"인증번호는 {code}입니다. {minutes}분 안에 입력해 주세요.")
            : ("Your verification code", $"Your verification code is {code}. It expires in {minutes} {(minutes == 1 ? "minute" : "minutes")}.");

        return request.Channel == MessageChannels.Sms
            ? MessagePayload.Serialize(new SmsMessage(request.TenantId, request.To, body))
            : MessagePayload.Serialize(new EmailMessage(request.TenantId, [request.To], subject, body));
    }

    private static string RequestHash(VerificationStart request, string destination)
    {
        var text = string.Join('|', request.Channel, destination, request.Purpose, (int)request.Ttl.TotalSeconds, request.CodeLength, request.MaxAttempts, request.Locale);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }
}
