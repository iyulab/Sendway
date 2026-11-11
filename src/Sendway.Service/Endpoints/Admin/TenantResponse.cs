namespace Sendway.Service.Endpoints.Admin;

// PreviousKeyExpiresAt is set while the key replaced by the last rotation still works.
// SmsCountries / *PerDay: the limits in effect (the tenant's own, or the deployment's default).
public sealed record TenantResponse(
    Guid Id, string Name, bool Active, bool Sandbox, DateTimeOffset CreatedAt, DateTimeOffset? PreviousKeyExpiresAt,
    IReadOnlyList<string> SmsCountries, int? SmsPerDay, int? EmailPerDay, int? PushPerDay,
    DestinationLimitsResponse Destination);

// Per destination, in effect: messages per rolling hour by channel, and the least seconds between two (null: none).
public sealed record DestinationLimitsResponse(int? SmsPerHour, int? EmailPerHour, int? PushPerHour, int? CooldownSeconds);
