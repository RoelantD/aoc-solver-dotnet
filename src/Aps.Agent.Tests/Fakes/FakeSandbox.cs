using Aps.Agent.Execution;

namespace Aps.Agent.Tests.Fakes;

/// <summary>One call made to the fake sandbox. <see cref="Cases"/> is null for raw runs.</summary>
public sealed record SandboxCall(string Code, IReadOnlyList<string>? Cases, int TimeoutSeconds, string SessionId);

/// <summary>
/// Test-only <see cref="ICodeExecutor"/>: it never executes code, it returns scripted outcomes in order.
/// Script with <see cref="Enqueue(ExecutionOutcome)"/> or <see cref="Enqueue(Func{SandboxCall, ExecutionOutcome})"/>;
/// a call with nothing scripted fails the test.
/// </summary>
public sealed class FakeSandbox : ICodeExecutor
{
    private readonly Queue<Func<SandboxCall, ExecutionOutcome>> _script = new();
    private int _sessions;

    public string Name => "fake-sandbox";

    public List<SandboxCall> Calls { get; } = [];

    public List<string> Released { get; } = [];

    public string NewSessionId() => $"aps-fake-{Interlocked.Increment(ref _sessions)}";

    public FakeSandbox Enqueue(ExecutionOutcome outcome) => Enqueue(_ => outcome);

    public FakeSandbox Enqueue(Func<SandboxCall, ExecutionOutcome> respond)
    {
        lock (_script)
            _script.Enqueue(respond);
        return this;
    }

    public Task<ExecutionOutcome> RunCasesAsync(string code, IReadOnlyList<string> cases, int timeoutSeconds, string sessionId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Next(new SandboxCall(code, [.. cases], timeoutSeconds, sessionId)));

    public Task<ExecutionOutcome> RunRawAsync(string code, int timeoutSeconds, string sessionId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Next(new SandboxCall(code, null, timeoutSeconds, sessionId)));

    public Task ReleaseAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        lock (Released)
            Released.Add(sessionId);
        return Task.CompletedTask;
    }

    private ExecutionOutcome Next(SandboxCall call)
    {
        Func<SandboxCall, ExecutionOutcome> respond;
        lock (_script)
        {
            Calls.Add(call);
            if (!_script.TryDequeue(out respond!))
                throw new InvalidOperationException($"FakeSandbox: no scripted outcome for call {Calls.Count}");
        }

        return respond(call);
    }

    // ---- Outcome builders

    /// <summary>Every case succeeded with the given outputs (in case order).</summary>
    public static ExecutionOutcome Ok(params string[] outputs) => new()
    {
        Status = ExecutionStatus.Ok,
        Results = [.. outputs.Select((o, i) => new CaseResult(i, o, null, 1))],
        CorrelationId = "fake",
    };

    public static ExecutionOutcome Timeout(int durationMs = 30_000) => new()
    {
        Status = ExecutionStatus.Timeout,
        TimedOut = true,
        DurationMs = durationMs,
        Stderr = "Execution timed out",
        CorrelationId = "fake",
    };
}
