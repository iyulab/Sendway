using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Sendway.Core;

namespace Sendway.Service.Health;

// When the service counts as unhealthy. Bound from "Sendway:Health".
public sealed class HealthOptions
{
    // A due item waiting longer than this means delivery has stopped or fallen behind. Items waiting
    // for a scheduled retry are not due, so a healthy service stays near zero whatever its load.
    public TimeSpan MaxOldestDue { get; set; } = TimeSpan.FromMinutes(5);

    // More items than this failed in the last 24 hours. Unset, failures are reported but not judged:
    // some failures (an address that does not exist) are normal, and how many is too many depends on
    // the deployment.
    public int? MaxFailedInLast24Hours { get; set; }
}

// Health endpoints without authentication, for probes and external monitoring. They say whether the
// service works and how its queues stand — counts and ages only, nothing about any tenant or message.
public static class SendwayHealth
{
    private const string Ready = "ready";
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(5);

    public static IServiceCollection AddSendwayHealthChecks(this IServiceCollection services)
    {
        services.AddOptions<HealthOptions>().BindConfiguration("Sendway:Health");
        services.AddSingleton<SharedQueueStats>();
        services.AddHealthChecks()
            .Add(new HealthCheckRegistration(
                "database",
                sp => new DatabaseHealthCheck(sp.GetRequiredService<IDbContextFactory<SendwayDbContext>>()),
                failureStatus: null,
                tags: [Ready],
                timeout: CheckTimeout))
            .Add(new HealthCheckRegistration(
                "messages",
                sp => new QueueHealthCheck(
                    ct => sp.GetRequiredService<SharedQueueStats>().GetAsync("messages", sp.GetRequiredService<IMessageOutbox>().GetStatsAsync, ct),
                    sp.GetRequiredService<IOptions<HealthOptions>>().Value),
                failureStatus: null,
                tags: null,
                timeout: CheckTimeout))
            .Add(new HealthCheckRegistration(
                "webhooks",
                sp => new QueueHealthCheck(
                    ct => sp.GetRequiredService<SharedQueueStats>().GetAsync("webhooks", sp.GetRequiredService<WebhookOutbox>().GetStatsAsync, ct),
                    sp.GetRequiredService<IOptions<HealthOptions>>().Value),
                failureStatus: null,
                tags: null,
                timeout: CheckTimeout));
        return services;
    }

    public static void MapSendwayHealthChecks(this IEndpointRouteBuilder app)
    {
        // The process is up and answering. For a liveness probe: it never consults the database, so
        // a database outage does not get healthy instances restarted.
        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false, ResponseWriter = WriteAsync });

        // This instance can take requests (the database is reachable). For a readiness probe: a
        // delivery backlog does not stop it accepting, so the backlog is not part of this answer.
        app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains(Ready), ResponseWriter = WriteAsync });

        // Everything, for external monitoring: anything short of Healthy answers 503, so a monitor
        // that only looks at the status code still alerts on a backlog or on failures.
        app.MapHealthChecks("/health", new HealthCheckOptions
        {
            ResponseWriter = WriteAsync,
            ResultStatusCodes =
            {
                [HealthStatus.Healthy] = StatusCodes.Status200OK,
                [HealthStatus.Degraded] = StatusCodes.Status503ServiceUnavailable,
                [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable
            }
        });
    }

    private static Task WriteAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";
        var body = new
        {
            status = report.Status.ToString(),
            checks = report.Entries.ToDictionary(
                entry => entry.Key,
                entry => new Dictionary<string, object?>(entry.Value.Data.Select(d => new KeyValuePair<string, object?>(d.Key, d.Value)))
                {
                    ["status"] = entry.Value.Status.ToString(),
                    ["description"] = entry.Value.Description
                })
        };
        return context.Response.WriteAsync(JsonSerializer.Serialize(body, JsonSerializerOptions.Web));
    }
}

public sealed class DatabaseHealthCheck(IDbContextFactory<SendwayDbContext> dbContextFactory) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            return await db.Database.CanConnectAsync(cancellationToken)
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("The database cannot be reached.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // The exception's text can carry connection details: the health check log records it, the
            // response (WriteAsync) leaves it out.
            return HealthCheckResult.Unhealthy("The database cannot be reached.", ex);
        }
    }
}

public sealed class QueueHealthCheck(Func<CancellationToken, Task<QueueStats>> getStats, HealthOptions options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var stats = await getStats(cancellationToken);
        var data = new Dictionary<string, object>
        {
            ["oldestDueSeconds"] = stats.OldestDueAge is { } age ? Math.Round(age.TotalSeconds, 1) : 0,
            ["failedInLast24Hours"] = stats.FailedInLast24Hours
        };

        if (stats.OldestDueAge > options.MaxOldestDue)
        {
            return HealthCheckResult.Unhealthy(
                $"An item has been due for {stats.OldestDueAge.Value.TotalSeconds:0} s, longer than {options.MaxOldestDue.TotalSeconds:0} s: delivery has stopped or fallen behind.",
                data: data);
        }

        if (stats.FailedInLast24Hours > options.MaxFailedInLast24Hours)
        {
            return HealthCheckResult.Degraded(
                $"{stats.FailedInLast24Hours} items failed in the last 24 hours, more than {options.MaxFailedInLast24Hours}.",
                data: data);
        }

        return HealthCheckResult.Healthy(data: data);
    }
}

// The health endpoints need no key, so without this anyone could make the database count both queues
// on every request — and most cheaply when it is already struggling. One count per queue per
// Lifetime is shared by every caller on this instance.
public sealed class SharedQueueStats(TimeProvider timeProvider)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(5);

    private readonly ConcurrentDictionary<string, (DateTimeOffset ReadAt, Task<QueueStats> Stats)> _latest = new();

    public Task<QueueStats> GetAsync(string queue, Func<CancellationToken, Task<QueueStats>> read, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        // The count runs without the caller's token because other callers share it; each caller
        // stops waiting on its own token.
        var entry = _latest.AddOrUpdate(
            queue,
            _ => (now, read(CancellationToken.None)),
            (_, latest) => now - latest.ReadAt < Lifetime && !latest.Stats.IsFaulted ? latest : (now, read(CancellationToken.None)));

        return entry.Stats.WaitAsync(cancellationToken);
    }
}
