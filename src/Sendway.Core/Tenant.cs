namespace Sendway.Core;

public sealed class Tenant
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public required string ApiKeyHash { get; set; }

    public required bool Active { get; set; }

    public required DateTimeOffset CreatedAt { get; init; }
}
