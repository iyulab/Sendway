namespace Sendway.Core;

// One administrative change: who made it, when, and to what. Never holds a secret — keys,
// credential bodies and signing secrets stay out of every field.
public sealed class AuditEvent
{
    public required Guid Id { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    // Who acted, as far as the service can tell (e.g. which administrator key).
    public required string Actor { get; init; }

    // What was done, e.g. "tenant.key_rotated".
    public required string Action { get; init; }

    // The tenant the change applies to; null for a deployment-wide change.
    public Guid? TenantId { get; init; }

    // What within the tenant or deployment it applies to (a channel, a URL), when that matters.
    public string? Subject { get; init; }

    // The address the request came from, as the service saw it.
    public string? RemoteAddress { get; init; }
}
