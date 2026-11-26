using Aps.Agent.Abstractions;

namespace Aps.Agent.Tests.Fakes;

/// <summary>Manually advanced clock for lease and cooldown tests. Delays complete immediately and advance time.</summary>
public sealed class LeaseClock : IClock
{
    public DateTimeOffset UtcNow { get; private set; } = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);

    public void Advance(TimeSpan by) => UtcNow += by;

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default)
    {
        if (delay > TimeSpan.Zero) UtcNow += delay;
        return Task.CompletedTask;
    }
}
