using Aps.Agent.Abstractions;

namespace Aps.Agent.Tests.Fakes;

/// <summary>Virtual time: <see cref="DelayAsync"/> records the delay and advances the clock instantly.</summary>
public sealed class FakeClock(DateTimeOffset? start = null) : IClock
{
    private readonly List<TimeSpan> _delays = [];

    public DateTimeOffset UtcNow { get; private set; } = start ?? new DateTimeOffset(2024, 12, 1, 5, 0, 0, TimeSpan.Zero);

    public IEnumerable<double> DelaySeconds => _delays.Select(d => d.TotalSeconds);

    public void Advance(TimeSpan by) => UtcNow += by;

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _delays.Add(delay);
        UtcNow += delay;
        return Task.CompletedTask;
    }
}
