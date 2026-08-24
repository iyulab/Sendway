using Sendway.Core;
using Xunit;

namespace Sendway.Core.Tests;

public class RetryPolicyTests
{
    [Fact]
    public async Task ExecuteAsync_SucceedsFirstTry_DoesNotRetry()
    {
        var callCount = 0;

        await RetryPolicy.ExecuteAsync(() =>
        {
            callCount++;
            return Task.CompletedTask;
        }, initialDelay: TimeSpan.Zero);

        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task ExecuteAsync_FailsThenSucceeds_RetriesAndSucceeds()
    {
        var callCount = 0;

        await RetryPolicy.ExecuteAsync(() =>
        {
            callCount++;
            if (callCount < 3)
            {
                throw new InvalidOperationException("simulated transient failure");
            }
            return Task.CompletedTask;
        }, maxAttempts: 3, initialDelay: TimeSpan.Zero);

        Assert.Equal(3, callCount);
    }

    [Fact]
    public async Task ExecuteAsync_AlwaysFails_ThrowsAfterMaxAttempts()
    {
        var callCount = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() => RetryPolicy.ExecuteAsync(() =>
        {
            callCount++;
            throw new InvalidOperationException("simulated persistent failure");
        }, maxAttempts: 3, initialDelay: TimeSpan.Zero));

        Assert.Equal(3, callCount);
    }

    [Fact]
    public async Task ExecuteAsync_InvalidRecipientException_ThrowsImmediatelyWithoutRetry()
    {
        var callCount = 0;

        await Assert.ThrowsAsync<InvalidRecipientException>(() => RetryPolicy.ExecuteAsync(() =>
        {
            callCount++;
            throw new InvalidRecipientException("bad address");
        }, maxAttempts: 3, initialDelay: TimeSpan.Zero));

        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task ExecuteAsync_Cancelled_ThrowsImmediatelyWithoutRetry()
    {
        var callCount = 0;
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => RetryPolicy.ExecuteAsync(() =>
        {
            callCount++;
            throw new OperationCanceledException();
        }, maxAttempts: 3, initialDelay: TimeSpan.Zero, cancellationToken: cts.Token));

        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task ExecuteAsync_BackoffDoublesBetweenAttempts()
    {
        var callCount = 0;
        var start = DateTime.UtcNow;

        await Assert.ThrowsAsync<InvalidOperationException>(() => RetryPolicy.ExecuteAsync(() =>
        {
            callCount++;
            throw new InvalidOperationException("simulated persistent failure");
        }, maxAttempts: 3, initialDelay: TimeSpan.FromMilliseconds(50)));

        var elapsed = DateTime.UtcNow - start;
        // Two delays between three attempts: 50ms + 100ms = 150ms minimum.
        Assert.True(elapsed >= TimeSpan.FromMilliseconds(140), $"Expected at least ~150ms elapsed, got {elapsed}");
        Assert.Equal(3, callCount);
    }
}
