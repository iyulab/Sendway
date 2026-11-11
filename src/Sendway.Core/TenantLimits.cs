namespace Sendway.Core;

// What a tenant may send, so a bug or a misuse in a calling service cannot run up a provider bill: the
// countries its SMS may go to, and how many messages per channel it may have accepted in the last 24
// hours. A tenant's own values (Tenant.SmsCountries / *PerDay) override the deployment's defaults; null
// means "the default".
public sealed class TenantLimitOptions
{
    // Country calling codes SMS may go to. Korea by default — an international number is a decision.
    public IReadOnlyList<string> SmsCountries { get; set; } = ["82"];

    // Messages accepted per channel in a rolling 24 hours; null is no limit. SMS costs money per
    // message, so it has one by default.
    public int? SmsPerDay { get; set; } = 1000;

    public int? EmailPerDay { get; set; }

    public int? PushPerDay { get; set; }

    // How often one destination (an email address, a phone number, a device token) may be sent to by one
    // tenant. Not the tenant's volume — that is *PerDay — but the pile-up on a single recipient: a
    // verification code requested ten times, a mailbox bombed through a sign-up form, a retry loop in the
    // caller. A message counts once it is accepted, whatever became of it.
    //
    // DestinationCooldown: the least time between two messages to the same destination; null is none (a
    // deployment cannot know how often its callers legitimately send, e.g. a sign-up mail and a welcome
    // mail seconds apart — a caller that does, such as a code resend, asks for one on its tenant).
    public TimeSpan? DestinationCooldown { get; set; }

    // Messages to one destination in a rolling hour; null is no limit. SMS costs money and a number is
    // easily abused as a nuisance, so it is tight; email and push leave room for a busy mailbox.
    public int? SmsPerDestinationPerHour { get; set; } = 5;

    public int? EmailPerDestinationPerHour { get; set; } = 30;

    public int? PushPerDestinationPerHour { get; set; } = 60;
}

public static class TenantLimits
{
    public static readonly TimeSpan Window = TimeSpan.FromHours(24);

    public static readonly TimeSpan DestinationWindow = TimeSpan.FromHours(1);

    public static IReadOnlyList<string> SmsCountries(Tenant tenant, TenantLimitOptions defaults) =>
        tenant.SmsCountries is { } own
            ? own.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : defaults.SmsCountries;

    // The tenant's own value applies to every channel; without one each channel has its default.
    public static int? PerDestinationPerHour(Tenant tenant, string channel, TenantLimitOptions defaults) =>
        tenant.DestinationPerHour ?? channel switch
        {
            MessageChannels.Sms => defaults.SmsPerDestinationPerHour,
            MessageChannels.Email => defaults.EmailPerDestinationPerHour,
            MessageChannels.Push => defaults.PushPerDestinationPerHour,
            _ => null
        };

    // The tenant's own value in seconds (0 turns the cooldown off for it), else the deployment's.
    public static TimeSpan? DestinationCooldown(Tenant tenant, TenantLimitOptions defaults) =>
        tenant.DestinationCooldownSeconds is { } seconds
            ? (seconds > 0 ? TimeSpan.FromSeconds(seconds) : null)
            : defaults.DestinationCooldown;

    public static int? PerDay(Tenant tenant, string channel, TenantLimitOptions defaults) => channel switch
    {
        MessageChannels.Sms => tenant.SmsPerDay ?? defaults.SmsPerDay,
        MessageChannels.Email => tenant.EmailPerDay ?? defaults.EmailPerDay,
        MessageChannels.Push => tenant.PushPerDay ?? defaults.PushPerDay,
        _ => null
    };
}
