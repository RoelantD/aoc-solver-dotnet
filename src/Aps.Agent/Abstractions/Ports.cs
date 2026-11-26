namespace Aps.Agent.Abstractions;

/// <summary>Reads secrets at runtime (Key Vault in Azure; a git-ignored .env locally).</summary>
public interface ISecretProvider
{
    /// <summary>The Advent of Code session cookie value. Never logged, never sent to models, sandbox or clients.</summary>
    ValueTask<string> GetAocSessionAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Private cache for puzzle pages and personal inputs, so each is fetched from Advent of Code only once. Never leaves
/// the store: puzzle text and inputs are not ours to redistribute.
/// </summary>
public interface IPuzzleCache
{
    /// <summary>Kind is "calendar", "page" or "input".</summary>
    ValueTask<string?> GetAsync(int year, int day, string kind, CancellationToken cancellationToken = default);

    ValueTask SetAsync(int year, int day, string kind, string content, CancellationToken cancellationToken = default);
}

/// <summary>Clock seam for pacing, cooldowns and leases.</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }

    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default);
}

public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default) =>
        delay <= TimeSpan.Zero ? Task.CompletedTask : Task.Delay(delay, cancellationToken);
}
