namespace Sendway.Core.Tests;

// A clock the test moves by hand, so retry schedules and lease expiry can be exercised without
// waiting for them.
internal sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
