using Sendway.Core;

namespace Sendway.Service.Endpoints.Admin;

public static class GetUsageEndpoint
{
    public const int DefaultDays = 30;
    public const int MaxDays = 366;

    // Which calling services actually send, and how their messages end — per tenant and channel. The
    // last successful send is over all time; the counts cover messages that finished in the last `days`.
    public static void MapGetUsageEndpoint(this IEndpointRouteBuilder group)
    {
        group.MapGet("/usage", async (int? days, IMessageUsage usage, TimeProvider timeProvider, CancellationToken cancellationToken) =>
        {
            if (days is < 1 or > MaxDays)
            {
                return ApiProblem.BadRequest($"days must be between 1 and {MaxDays}.");
            }

            var since = timeProvider.GetUtcNow().AddDays(-(days ?? DefaultDays));
            var tenants = await usage.GetAsync(since, cancellationToken);

            return Results.Ok(new UsageResponse(
                since,
                tenants.Select(t => new TenantUsageResponse(
                    t.TenantId,
                    t.Name,
                    t.Active,
                    t.LastSentAt,
                    t.Channels.ToDictionary(c => c.Key, c => new ChannelUsageResponse(c.Value.Sent, c.Value.Failed))))));
        });
    }
}

public sealed record UsageResponse(DateTimeOffset Since, IEnumerable<TenantUsageResponse> Tenants);

public sealed record TenantUsageResponse(
    Guid TenantId,
    string Name,
    bool Active,
    DateTimeOffset? LastSentAt,
    IReadOnlyDictionary<string, ChannelUsageResponse> Channels);

public sealed record ChannelUsageResponse(int Sent, int Failed);
