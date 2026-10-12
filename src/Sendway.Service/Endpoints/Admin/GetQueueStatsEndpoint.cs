using Sendway.Core;

namespace Sendway.Service.Endpoints.Admin;

public static class GetQueueStatsEndpoint
{
    // Answers the operator's question about an accept-then-deliver service: is delivery keeping up,
    // or is something piling up? Counts and one age per queue — enough to alert on, without exposing
    // any message.
    public static void MapGetQueueStatsEndpoint(this IEndpointRouteBuilder group)
    {
        group.MapGet("/queues", async (IMessageOutbox messages, WebhookOutbox webhooks, CancellationToken cancellationToken) =>
            Results.Ok(new QueuesResponse(
                ToResponse(await messages.GetStatsAsync(cancellationToken)),
                ToResponse(await webhooks.GetStatsAsync(cancellationToken)))));
    }

    private static QueueStatsResponse ToResponse(QueueStats stats) =>
        new(stats.Queued, stats.Sending, stats.FailedInLast24Hours, stats.OldestDueAge?.TotalSeconds);
}

public sealed record QueuesResponse(QueueStatsResponse Messages, QueueStatsResponse Webhooks);

public sealed record QueueStatsResponse(int Queued, int Sending, int FailedInLast24Hours, double? OldestDueSeconds);
