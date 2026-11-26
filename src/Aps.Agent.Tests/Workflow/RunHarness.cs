using Aps.Agent.Agents;
using Aps.Agent.Aoc;
using Aps.Agent.Domain;
using Aps.Agent.Execution;
using Aps.Agent.Persistence;
using Aps.Agent.Tests.Fakes;
using Aps.Agent.Verification;
using Aps.Agent.Workflow;

namespace Aps.Agent.Tests.Workflow;

/// <summary>
/// Wires the real orchestrator, part workflow, lease, event log and submission gate to fakes: scripted models, a fake
/// sandbox that answers by input, the fake Advent of Code site and the in-memory store. No network.
/// </summary>
public sealed class RunHarness : IDisposable
{
    public InMemoryStore Store { get; } = new();
    public EventLog Events { get; }
    public ScriptedTeam Team { get; } = new();
    public FakeSandbox Sandbox { get; } = new();
    public FakeAocHandler Site { get; }
    public FakeClock Clock { get; } = new();
    public RunControl Control { get; } = new();
    public AocClient Aoc { get; }

    /// <summary>Sandbox answers: input text → output (or the special value "!error" / "!timeout").</summary>
    public Dictionary<string, string> SandboxAnswers { get; } = [];

    /// <summary>Answers for programs containing "products" (Part 2 of the synthetic day 1).</summary>
    public Dictionary<string, string> Part2Answers { get; } = new() { [Day1.ExampleInput] = "104", [Day1.RealInput] = "287" };

    public RunHarness(FakeAocHandler? site = null)
    {
        Site = site ?? new FakeAocHandler { Puzzles = { [1] = FakePuzzle.SyntheticDay1(), [2] = FakePuzzle.SyntheticDay2() } };
        Events = new EventLog(Store.Events, TimeSpan.FromMilliseconds(20));
        Aoc = new AocClient(new FakeSecretProvider("test-session-cookie"), Store.Cache, Clock,
            new AocClientOptions { Contact = "tests@example.invalid", MinInterval = TimeSpan.FromSeconds(1) }, Site);
    }

    /// <summary>Scripts the sandbox to answer per input; codes containing "WRONG" return "0".</summary>
    public RunHarness AnswerBy(params (string Input, string Output)[] answers)
    {
        foreach (var (i, o) in answers) SandboxAnswers[i] = o;
        for (var n = 0; n < 40; n++)
            Sandbox.Enqueue(call => call.Code.Contains("WRONG", StringComparison.Ordinal)
                ? FakeSandbox.Ok([.. call.Cases!.Select(_ => "0")])
                : call.Code.Contains("SLOW", StringComparison.Ordinal)
                    ? FakeSandbox.Timeout()
                    : FakeSandbox.Ok([.. call.Cases!.Select(c =>
                        (call.Code.Contains("products", StringComparison.Ordinal) ? Part2Answers : SandboxAnswers).GetValueOrDefault(c, "?"))]));
        return this;
    }

    public async Task<Run> CreateRunAsync(int dayFrom = 1, int dayTo = 1, RunConfig? config = null, CancellationToken ct = default)
    {
        var run = await Store.Runs.CreateAsync(new Run
        {
            Id = $"run-{Guid.NewGuid():n}"[..16], Year = 2024, DayFrom = dayFrom, DayTo = dayTo, AutoSubmit = true,
            Config = (config ?? new RunConfig { AocMinIntervalS = 1 }).Validated(), CreatedAt = Clock.UtcNow, AuthorizedAt = Clock.UtcNow,
        }, ct);
        return run;
    }

    public SolverServices Services(string owner = "test-owner")
    {
        var lease = new RunLease(Store.Runs, owner, Clock);
        return new SolverServices(Store, Events, lease, new AgentRunner(Team), Team, Sandbox, Aoc,
            new SubmissionGate(Store.Ledger, Clock), Control, Clock);
    }

    public async Task<RunStatus> RunAsync(string runId, CancellationToken ct, string owner = "test-owner")
    {
        var svc = Services(owner);
        Assert.True(await svc.Lease.TryAcquireAsync(runId, ct));
        return await new RunOrchestrator(svc, runId).RunAsync(ct);
    }

    public async Task<IReadOnlyList<DomainEvent>> EventsAsync(string runId, CancellationToken ct) => await Store.Events.ReadAfterAsync(runId, 0, 10_000, ct);

    public void Dispose() => Aoc.Dispose();
}

public static class Day1
{
    public const string ExampleInput = "3 5\n10 4\n7 7\n";
    public const string RealInput = "1 4\n9 2\n5 5\n12 20\n";
    public const string GoodCode = "def solve(input_text):\n    return str(sum(abs(int(a)-int(b)) for a,b in (l.split() for l in input_text.split('\\n') if l)))";
    public const string WrongCode = "def solve(input_text):\n    return '0'  # WRONG";
    public const string Part2Code = "def solve(input_text):\n    return 'products'";
}
