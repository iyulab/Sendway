using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sendway.Service.Health;
using Xunit;

namespace Sendway.Service.Tests;

// What the service writes to its log for requests and for its own health: probes leave no request line,
// every other request leaves exactly one, and a heartbeat line says how the service stands.
public class RequestLoggingTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly TestIsolatedStorage _storage = new();
    private readonly CapturedLogs _logs = new();
    private readonly WebApplicationFactory<Program> _factory;

    public RequestLoggingTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((context, configuration) => _storage.Apply(context, configuration));
            builder.ConfigureLogging(logging => logging.AddProvider(_logs));
        });
    }

    public void Dispose() => _storage.Dispose();

    [Theory]
    [InlineData("/health")]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task A_health_probe_leaves_no_request_line(string path)
    {
        using var client = _factory.CreateClient();

        await client.GetAsync(path);

        Assert.DoesNotContain(_logs.Entries, e => e.Category.StartsWith("Microsoft.AspNetCore.Hosting", StringComparison.Ordinal) && e.Message.Contains(path, StringComparison.Ordinal));
        Assert.DoesNotContain(_logs.Entries, e => e.Category.Contains("HttpLogging", StringComparison.Ordinal) && e.Message.Contains(path, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Another_request_leaves_one_line_with_method_path_status_and_duration()
    {
        using var client = _factory.CreateClient();

        await client.GetAsync($"/messages/{Guid.NewGuid()}?secret=1");

        var lines = _logs.Entries.Where(e => e.Category.Contains("HttpLogging", StringComparison.Ordinal)).ToList();
        var line = Assert.Single(lines);
        Assert.Contains("GET", line.Message, StringComparison.Ordinal);
        Assert.Contains("/messages/", line.Message, StringComparison.Ordinal);
        Assert.Contains("401", line.Message, StringComparison.Ordinal);
        Assert.Contains("Duration", line.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", line.Message, StringComparison.Ordinal);

        // The host's own "Request starting/finished" pair is off by default.
        Assert.DoesNotContain(_logs.Entries, e => e.Category == "Microsoft.AspNetCore.Hosting.Diagnostics" && e.Level < LogLevel.Warning);
    }

    [Fact]
    public async Task The_request_line_carries_no_key_and_no_body()
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/messages/email")
        {
            Content = new StringContent("""{"body":"a-message-text-that-must-not-be-logged"}""", System.Text.Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-Api-Key", "sw_a-key-that-must-not-be-logged");

        await client.SendAsync(request);

        Assert.DoesNotContain(_logs.Entries, e => e.Message.Contains("must-not-be-logged", StringComparison.Ordinal));
    }

    [Fact]
    public void The_heartbeat_period_is_a_minute_and_never_shorter_than_the_minimum()
    {
        Assert.Equal(TimeSpan.FromMinutes(1), _factory.Services.GetRequiredService<IOptions<HealthCheckPublisherOptions>>().Value.Period);

        using var fast = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Sendway:Health:HeartbeatInterval"] = "00:00:01" })));
        Assert.Equal(HealthOptions.MinHeartbeat, fast.Services.GetRequiredService<IOptions<HealthCheckPublisherOptions>>().Value.Period);
    }

    [Theory]
    [InlineData(HealthStatus.Healthy, LogLevel.Information)]
    [InlineData(HealthStatus.Degraded, LogLevel.Warning)]
    [InlineData(HealthStatus.Unhealthy, LogLevel.Error)]
    public async Task The_heartbeat_line_names_the_status_and_every_check_and_nothing_else(HealthStatus status, LogLevel expected)
    {
        var logs = new CapturedLogs();
        var publisher = new HealthHeartbeatPublisher(logs.CreateLogger<HealthHeartbeatPublisher>());
        var report = new HealthReport(
            new Dictionary<string, HealthReportEntry>
            {
                ["messages"] = new(HealthStatus.Healthy, "queue is fine", TimeSpan.Zero, null, null),
                ["database"] = new(status, "The database cannot be reached.", TimeSpan.Zero, new InvalidOperationException("Host=db.internal;Password=hunter2"), null)
            },
            status,
            TimeSpan.Zero);

        await publisher.PublishAsync(report, CancellationToken.None);

        var entry = Assert.Single(logs.Entries);
        Assert.Equal(expected, entry.Level);
        Assert.Equal($"Sendway health {status}: database={status} messages=Healthy", entry.Message);
        Assert.DoesNotContain("hunter2", entry.Message, StringComparison.Ordinal);
    }

    private sealed class CapturedLogs : ILoggerProvider
    {
        public ConcurrentQueue<(string Category, LogLevel Level, string Message)> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Capture(categoryName, Entries);

        public ILogger<T> CreateLogger<T>() => new Typed<T>(CreateLogger(typeof(T).FullName!));

        private sealed class Typed<T>(ILogger inner) : ILogger<T>
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(state);

            public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                inner.Log(logLevel, eventId, state, exception, formatter);
        }

        public void Dispose()
        {
        }

        private sealed class Capture(string category, ConcurrentQueue<(string, LogLevel, string)> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                entries.Enqueue((category, logLevel, formatter(state, exception)));
        }
    }
}
