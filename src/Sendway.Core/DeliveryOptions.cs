namespace Sendway.Core;

// The attempt policy of one delivery queue.
public abstract class DeliveryOptions
{
    // Attempts per item before it is marked Failed.
    public abstract int MaxAttempts { get; set; }

    // Upper bound on a single attempt. Client library defaults run to minutes; without this one
    // hung connection would hold a delivery slot indefinitely.
    public abstract TimeSpan AttemptTimeout { get; set; }

    // Delay before the second attempt; each later retry waits RetryBackoffFactor times longer.
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(5);

    public double RetryBackoffFactor { get; set; } = 6;

    // How many items are delivered at once, so one slow call doesn't hold up every other tenant.
    public abstract int Concurrency { get; set; }
}

public sealed class MessageDeliveryOptions : DeliveryOptions
{
    // With the default delays (5s, 30s, 3m, 18m), a message that keeps failing is retried over
    // roughly 22 minutes — long enough to ride out a provider blip or a redeploy, short enough that
    // a transactional message isn't delivered long after it stopped being useful.
    public override int MaxAttempts { get; set; } = 5;

    public override TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public override int Concurrency { get; set; } = 4;

    // How often an idle worker checks each queue for due items on its own. New items wake a worker
    // immediately; this interval is what picks up scheduled retries and expired leases.
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    // Set to false to run an instance that only accepts messages and leaves delivery (of messages
    // and of webhook events) to others.
    public bool WorkerEnabled { get; set; } = true;
}

public sealed class WebhookDeliveryOptions : DeliveryOptions
{
    // An event is a notification of something that already happened, so it is worth trying for
    // longer than the message itself: with the default delays (5s, 30s, 3m, 18m, 1h48m) a receiver
    // that is down gets a little over two hours to come back.
    public override int MaxAttempts { get; set; } = 6;

    // Within the 15–30 second range the Standard Webhooks specification recommends for senders.
    public override TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromSeconds(15);

    public override int Concurrency { get; set; } = 2;
}
