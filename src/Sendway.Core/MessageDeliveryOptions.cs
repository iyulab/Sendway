namespace Sendway.Core;

public sealed class MessageDeliveryOptions
{
    // Attempts per message before it is marked Failed. With the default delays below, a message
    // that keeps failing is retried over roughly 22 minutes — long enough to ride out a provider
    // blip or a redeploy, short enough that a transactional message isn't delivered long after it
    // stopped being useful.
    public int MaxAttempts { get; set; } = 5;

    // Upper bound on a single provider call. Provider client defaults run to minutes; without this
    // one hung connection would hold a delivery slot indefinitely.
    public TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromSeconds(30);

    // Delay before the second attempt; each later retry waits RetryBackoffFactor times longer
    // (5s, 30s, 3m, 18m with the defaults).
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(5);

    public double RetryBackoffFactor { get; set; } = 6;

    // How many messages are delivered at once, so one slow provider call doesn't hold up every
    // other tenant's messages.
    public int Concurrency { get; set; } = 4;

    // How often an idle worker checks for due messages on its own. New messages wake a worker
    // immediately; this interval is what picks up scheduled retries and expired leases.
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    // Set to false to run an instance that only accepts messages and leaves delivery to others.
    public bool WorkerEnabled { get; set; } = true;
}
