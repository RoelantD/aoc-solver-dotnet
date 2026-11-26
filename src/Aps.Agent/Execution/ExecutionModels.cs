namespace Aps.Agent.Execution;

/// <summary>Outcome of one sandbox execution.</summary>
public enum ExecutionStatus
{
    Ok,
    Error,
    Timeout,
}

/// <summary>Result of one <c>solve(text)</c> call inside the harness.</summary>
public sealed record CaseResult(int Index, string? Output, string? Error, int DurationMs);

public sealed record ExecutionOutcome
{
    public required ExecutionStatus Status { get; init; }

    public IReadOnlyList<CaseResult> Results { get; init; } = [];

    /// <summary>Set when the program failed to load or does not define <c>solve</c>.</summary>
    public string? LoadError { get; init; }

    public string Stdout { get; init; } = "";

    public string Stderr { get; init; } = "";

    public int DurationMs { get; init; }

    public bool TimedOut { get; init; }

    /// <summary>The sandbox session identifier the execution ran under.</summary>
    public string CorrelationId { get; init; } = "";
}

/// <summary>The sandbox service is unavailable. This is not a solving failure and does not count as an attempt.</summary>
public sealed class SandboxInfraException(string message, int? statusCode = null, TimeSpan? retryAfter = null, Exception? inner = null)
    : Exception(message, inner)
{
    public int? StatusCode { get; } = statusCode;

    /// <summary>How long the service asked us to wait (or the last back-off used), when known.</summary>
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

/// <summary>
/// Runs untrusted generated Python remotely. There is intentionally no local or in-process
/// implementation: model-written code never runs on the agent's host, next to its credentials.
/// </summary>
public interface ICodeExecutor
{
    string Name { get; }

    /// <summary>A fresh session identifier (<c>aps-{guid}</c>); use one per attempt.</summary>
    string NewSessionId();

    /// <summary>Wraps <paramref name="code"/> in the harness and calls <c>solve</c> once per case.</summary>
    Task<ExecutionOutcome> RunCasesAsync(string code, IReadOnlyList<string> cases, int timeoutSeconds, string sessionId,
        CancellationToken cancellationToken = default);

    /// <summary>Runs <paramref name="code"/> as-is (used for probes).</summary>
    Task<ExecutionOutcome> RunRawAsync(string code, int timeoutSeconds, string sessionId,
        CancellationToken cancellationToken = default);

    /// <summary>Best-effort early release of a session. Never throws.</summary>
    Task ReleaseAsync(string sessionId, CancellationToken cancellationToken = default);
}
