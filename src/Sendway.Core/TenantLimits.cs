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
}

public static class TenantLimits
{
    public static readonly TimeSpan Window = TimeSpan.FromHours(24);

    public static IReadOnlyList<string> SmsCountries(Tenant tenant, TenantLimitOptions defaults) =>
        tenant.SmsCountries is { } own
            ? own.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : defaults.SmsCountries;

    public static int? PerDay(Tenant tenant, string channel, TenantLimitOptions defaults) => channel switch
    {
        MessageChannels.Sms => tenant.SmsPerDay ?? defaults.SmsPerDay,
        MessageChannels.Email => tenant.EmailPerDay ?? defaults.EmailPerDay,
        MessageChannels.Push => tenant.PushPerDay ?? defaults.PushPerDay,
        _ => null
    };
}
