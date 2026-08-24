namespace Sendway.Core;

// Shared by SmtpEmailSender and FcmPushSender. Any failure except InvalidRecipientException (the
// input itself is bad — retrying with the same input fails identically) or cancellation is treated
// as transient (network blip, upstream briefly unreachable) and retried with exponential backoff.
public static class RetryPolicy
{
    public static async Task ExecuteAsync(
        Func<Task> action,
        int maxAttempts = 3,
        TimeSpan? initialDelay = null,
        CancellationToken cancellationToken = default)
    {
        var delay = initialDelay ?? TimeSpan.FromMilliseconds(200);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await action();
                return;
            }
            catch (InvalidRecipientException)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch when (attempt < maxAttempts)
            {
                await Task.Delay(delay, cancellationToken);
                delay += delay;
            }
        }
    }
}
