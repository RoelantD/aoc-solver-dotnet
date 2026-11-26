using System.Security.Cryptography;
using System.Text;
using Aps.Agent.Agents;
using Aps.Agent.Aoc;
using Aps.Agent.Domain;
using Aps.Agent.Execution;
using Aps.Agent.Observability;
using Aps.Agent.Verification;
using Microsoft.Agents.AI.Workflows;
using MafWorkflow = Microsoft.Agents.AI.Workflows.Workflow;

namespace Aps.Agent.Workflow;

// ---- messages between the steps of one part
public sealed record StartPart(string? PendingAnswer = null);
public sealed record AnalysisReady;
public sealed record CodeRequest;
public sealed record CodeReady(int Revision, string Code);
public sealed record TestsPassed(int Revision, string Code);
public sealed record CandidateReady(int Revision, string Code, string Answer);
public sealed record ApprovalDecision(CandidateReady Candidate, bool Approved, string? DecidedBy);

public enum PartResult { Verified, Failed, Blocked }

/// <summary>Final output of a part workflow.</summary>
public sealed record PartOutcome(PartResult Result, string Reason, string? Answer = null);

/// <summary>
/// The per-part Agent Framework workflow:
/// <code>
/// Analyst → VerifyExamples → Coder ⇄ SandboxTest → RunInput → [Approval port, manual mode] → Submit
///                              ↑______________ repair / handoff / rejection ______________|
/// </code>
/// Agents: Analyst, Coder, Escalation Coder (inside the Coder step, chosen by the deterministic policy).
/// Everything else is a deterministic system step. Executors communicate only through typed messages; the edge
/// conditions below route on the message type, which is what makes the repair loops possible.
/// </summary>
public static class PartWorkflow
{
    public const string ApprovalPortId = "approval";

    public static MafWorkflow Build(PartSession s)
    {
        var analyst = new AnalystExecutor(s);
        var verify = new VerifyExamplesExecutor(s);
        var coder = new CoderExecutor(s);
        var test = new SandboxTestExecutor(s);
        var runInput = new RunInputExecutor(s);
        // A RequestPort is how a workflow asks the outside world: sending to it surfaces a RequestInfoEvent to the
        // caller, and the workflow waits (and can be checkpointed) until a response with the same RequestId arrives.
        var approval = RequestPort.Create<CandidateReady, ApprovalDecision>(ApprovalPortId);
        var submit = new SubmitExecutor(s);
        var manual = s.Cfg.ApprovalMode == ApprovalMode.Manual;

        return new WorkflowBuilder(analyst)
            .WithName($"aps-part-d{s.Day:D2}-p{s.Part}")
            .AddEdge<object>(analyst, verify, m => m is AnalysisReady)
            .AddEdge<object>(analyst, submit, m => m is CandidateReady) // resume with a pending (uncertain) answer
            .AddEdge<object>(verify, coder, m => m is CodeRequest)
            .AddEdge<object>(coder, test, m => m is CodeReady)
            .AddEdge<object>(test, coder, m => m is CodeRequest)
            .AddEdge<object>(test, runInput, m => m is TestsPassed)
            .AddEdge<object>(runInput, coder, m => m is CodeRequest)
            .AddEdge<object>(runInput, approval, m => manual && m is CandidateReady)
            .AddEdge<object>(runInput, submit, m => !manual && m is CandidateReady)
            .AddEdge(approval, submit)
            .AddEdge<object>(submit, coder, m => m is CodeRequest)
            .WithOutputFrom(coder, submit)
            .Build();
    }

    internal static string Short(string? s, int n = 160)
    {
        var t = (s ?? "").Trim().Replace('\n', ' ');
        return t.Length <= n ? t : t[..(n - 1)] + "…";
    }
}

/// <summary>
/// Base for the part's executors. MAF only routes message and output types an executor declares in its protocol,
/// so every type the steps may send is declared once here.
/// </summary>
public abstract class PartStep<TIn>(string id, PartSession session) : Executor<TIn>(id)
{
    protected PartSession S { get; } = session;

    protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder protocolBuilder) =>
        base.ConfigureProtocol(protocolBuilder)
            .SendsMessageTypes([typeof(AnalysisReady), typeof(CodeRequest), typeof(CodeReady), typeof(TestsPassed), typeof(CandidateReady)])
            .YieldsOutput<PartOutcome>();
}

/// <summary>
/// Analyst agent: understands the puzzle and proposes examples. MAF calls the start executor's
/// <c>OnCheckpointingAsync</c> at every superstep, not only when it ran, so this executor also carries the part's
/// working memory (<see cref="PartMemory"/>) into each checkpoint and back out on restore.
/// </summary>
public sealed class AnalystExecutor(PartSession s) : PartStep<StartPart>("analyst", s)
{
    public const string MemoryKey = "part-memory";

    protected override ValueTask OnCheckpointingAsync(IWorkflowContext context, CancellationToken ct = default) =>
        context.QueueStateUpdateAsync(MemoryKey, S.Snapshot(), ct);

    protected override async ValueTask OnCheckpointRestoredAsync(IWorkflowContext context, CancellationToken ct = default)
    {
        if (await context.ReadStateAsync<PartMemory>(MemoryKey, ct) is { } memory) S.Restore(memory);
    }

    public override async ValueTask HandleAsync(StartPart start, IWorkflowContext context, CancellationToken ct = default)
    {
        if (start.PendingAnswer is { } pending)
        {
            // An earlier submission of this answer has an unknown outcome: go straight to the submit step, where the
            // gate reconciles it with the account state before anything is POSTed again.
            await context.SendMessageAsync(new CandidateReady(0, "", pending), ct);
            return;
        }
        S.PausePoint();
        await S.SetStateAsync(PartState.ANALYZING, null, ct);
        await S.SetActiveAgentAsync(AgentIds.Analyst, ct);
        await S.EmitAsync("agent.step", $"Analyst ({S.ModelDisplay(AgentIds.Analyst)}) is reading the puzzle", agent: AgentIds.Analyst, ct: ct);
        try
        {
            var analysis = await S.S.Runner.RunAsync<Analysis>(AgentIds.Analyst, Prompts.Analyst, Prompts.AnalystUser(S.Prompt()),
                "analyze", S, ct);
            S.Analysis = analysis;
            await S.SaveAsync(p => p with { Understanding = System.Text.Json.JsonSerializer.SerializeToElement(analysis) }, ct);
            await S.EmitAsync("agent.analysis", $"Approach: {PartWorkflow.Short(analysis.Algorithm, 220)}", agent: AgentIds.Analyst,
                data: new { summary = analysis.Summary, algorithm = analysis.Algorithm, complexity = analysis.Complexity, model = S.ModelDisplay(AgentIds.Analyst) },
                ct: ct);
        }
        catch (ModelOutputException e)
        {
            // The Coder can still work from the puzzle text; examples are then unavailable.
            await S.EmitAsync("model.note", $"Analyst output unusable: {PartWorkflow.Short(e.Message)}", "warning", AgentIds.Analyst, ct: ct);
        }
        await context.SendMessageAsync(new AnalysisReady(), ct);
    }
}

/// <summary>System step: keep only examples that appear verbatim in the puzzle, so a hallucinated example cannot steer the Coder.</summary>
public sealed class VerifyExamplesExecutor(PartSession s) : PartStep<AnalysisReady>("verify-examples", s)
{
    /// <summary>
    /// Removes markup remnants a model may copy from the rendered page (e.g. "&gt;8" from &lt;em&gt;8&lt;/em&gt;).
    /// The verifier still requires the cleaned value to appear in the puzzle text.
    /// </summary>
    internal static string? CleanExpected(string? expected) =>
        expected is null ? null : System.Text.RegularExpressions.Regex.Replace(expected, "<[^>]*>", "").Trim().Trim('<', '>', '`', '"', '\'', '*').Trim();

    public override async ValueTask HandleAsync(AnalysisReady message, IWorkflowContext context, CancellationToken ct = default)
    {
        await S.SetActiveAgentAsync(AgentIds.System, ct);
        var proposed = S.Analysis?.Examples.Select(e => new ProposedExample(e.Input, CleanExpected(e.ExpectedOutput), e.Note)).ToList() ?? [];
        var (accepted, notes) = ExampleVerifier.Verify(proposed, S.Puzzle.PreBlocks, S.Puzzle.Emphasized, S.Puzzle.ArticleText);
        S.Examples.Clear();
        S.Examples.AddRange(accepted);
        foreach (var note in notes) await S.EmitAsync("test.note", note, "warning", AgentIds.System, ct: ct);
        if (accepted.Count == 0)
            await S.EmitAsync("test.unavailable", "No verifiable examples found in the puzzle text", "warning", AgentIds.System,
                new { reason = "no examples" }, ct: ct);
        await context.SendMessageAsync(new CodeRequest(), ct);
    }
}

/// <summary>Coder or Escalation Coder writes the program; the deterministic policy decides who and when to hand off.</summary>
public sealed class CoderExecutor(PartSession s) : PartStep<CodeRequest>("coder", s)
{
    public override async ValueTask HandleAsync(CodeRequest message, IWorkflowContext context, CancellationToken ct = default)
    {
        S.PausePoint();
        if (S.Progress.AttemptsUsed >= S.Cfg.MaxAttemptsPerPart)
        {
            await context.YieldOutputAsync(new PartOutcome(PartResult.Failed, $"no candidate after {S.Progress.AttemptsUsed} attempts"), ct);
            return;
        }
        var number = S.Progress.AttemptsUsed + 1;
        var plan = EscalationPolicy.Plan(number, S.Cfg, S.LastFailure, S.Progress.Escalated, S.S.Team.EscalationAvailable, S.AfterRejection);

        if (plan.HandoffReason is { } reason && !S.Progress.Escalated)
            await HandoffAsync(reason, number, ct);
        else
            await S.SetStateAsync(number == 1 ? PartState.GENERATING : PartState.REPAIRING, null, ct);

        await S.SetActiveAgentAsync(plan.Agent, ct);
        await S.SaveAsync(p => p with { AttemptsUsed = number, CurrentModel = S.S.Team.ModelFor(plan.Agent).Key }, ct);
        var attempt = new Attempt
        {
            Id = $"att-{S.Day:D2}-{S.Part}-{number}-{Guid.NewGuid().ToString("n")[..8]}", RunId = S.Run.Id, Day = S.Day, Part = S.Part, Number = number,
            AttemptKind = plan.Kind, Agent = plan.Agent, ModelKey = S.S.Team.ModelFor(plan.Agent).Key, StartedAt = S.S.Clock.UtcNow,
        };
        S.CurrentAttempt = attempt;
        await S.S.Store.Parts.UpsertAttemptAsync(attempt, ct);
        var who = $"{AgentIds.DisplayName(plan.Agent)} ({S.ModelDisplay(plan.Agent)})";
        await S.EmitAsync("agent.step", number == 1 ? $"{who} is writing a solution" : $"{who} is revising the solution", agent: plan.Agent, ct: ct);

        ProgramProposal program;
        try
        {
            var ledger = await S.S.Gate.LedgerForAsync(S.Year, S.Day, S.Part, ct);
            var prompt = S.Prompt() with { LedgerFeedback = ledger.Feedback(), EscalationReason = plan.HandoffReason };
            program = await S.S.Runner.RunAsync<ProgramProposal>(plan.Agent, Prompts.Coder(S.Cfg.ExecTimeoutS), Prompts.CoderUser(prompt),
                plan.Agent == AgentIds.EscalationCoder ? "escalate" : number == 1 ? "generate" : "repair", S, ct);
        }
        catch (ModelInfraException e)
        {
            // Infrastructure problems do not consume a solving attempt; the run pauses until the model is reachable.
            await S.SaveAsync(p => p with { AttemptsUsed = number - 1 }, ct);
            await S.S.Store.Parts.UpsertAttemptAsync(attempt with { Outcome = "infra_error", FinishedAt = S.S.Clock.UtcNow }, ct);
            throw new PauseRunException($"{who} unavailable: {e.Message}");
        }
        catch (ModelOutputException e)
        {
            S.Feedback.Add($"Your previous response was unusable: {e.Message}");
            S.LastFailure = "invalid_output";
            await S.EmitAsync("agent.repair", $"Model output unusable: {PartWorkflow.Short(e.Message)}", "warning", plan.Agent,
                new { attempt = number, reason = e.Message.Length > 300 ? e.Message[..300] : e.Message }, ct: ct);
            await FinishAttemptAsync("invalid_output", null, ct);
            await context.SendMessageAsync(new CodeRequest(), ct);
            return;
        }

        var revisions = await S.S.Store.Parts.ListRevisionsAsync(S.Run.Id, S.Day, S.Part, ct);
        var revision = revisions.Count + 1;
        var sha = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(program.Code)));
        await S.S.Store.Parts.UpsertRevisionAsync(new CodeRevision
        {
            Id = $"rev-{S.Day:D2}-{S.Part}-{revision}", RunId = S.Run.Id, Day = S.Day, Part = S.Part, Revision = revision,
            Agent = plan.Agent, ModelKey = attempt.ModelKey, Code = program.Code, Sha256 = sha, CreatedAt = S.S.Clock.UtcNow,
        }, ct);
        S.CurrentAttempt = attempt with { Algorithm = program.Algorithm, Diagnosis = program.Diagnosis };
        var lines = program.Code.Split('\n').Length;
        await S.EmitAsync("agent.code", $"{AgentIds.DisplayName(plan.Agent)} wrote revision {revision} ({lines} lines)", agent: plan.Agent,
            data: new { revision, sha256 = sha, lines, model = S.ModelDisplay(plan.Agent), diagnosis = program.Diagnosis }, ct: ct);
        S.PreviousCode = program.Code;
        if (!program.Code.Contains("def solve", StringComparison.Ordinal))
        {
            S.Feedback.Add("The module must define solve(input_text: str) -> str.");
            S.LastFailure = "invalid_output";
            await FinishAttemptAsync("invalid_output", program.Algorithm, ct);
            await context.SendMessageAsync(new CodeRequest(), ct);
            return;
        }
        await context.SendMessageAsync(new CodeReady(revision, program.Code), ct);
    }

    /// <summary>
    /// The handoff: the Coder writes a bounded handover summary and the Escalation Coder takes over with that summary
    /// instead of the full conversation, which keeps its context (and cost) small.
    /// </summary>
    private async Task HandoffAsync(string reason, int number, CancellationToken ct)
    {
        await S.SetStateAsync(PartState.ESCALATING, reason, ct);
        string summary;
        try
        {
            var handover = await S.S.Runner.RunAsync<Handover>(AgentIds.Coder, Prompts.HandoverInstructions, Prompts.HandoverUser(S.Prompt()),
                "handover", S, ct);
            summary = handover.ToSummary();
        }
        catch (ModelOutputException)
        {
            summary = string.Join("\n", S.History.TakeLast(4).Select(h => $"attempt {h.Number}: {h.Algorithm} → {h.Outcome}"));
        }
        if (summary.Length > 6000) summary = summary[..6000];
        S.HandoverSummary = summary;
        await S.SaveAsync(p => p with
        {
            Escalated = true,
            Handoffs = [.. p.Handoffs, new Handoff(AgentIds.Coder, AgentIds.EscalationCoder, reason, summary, S.S.Clock.UtcNow)],
        }, ct);
        var from = S.ModelDisplay(AgentIds.Coder);
        var to = S.ModelDisplay(AgentIds.EscalationCoder);
        await S.EmitAsync("agent.handoff", $"Coder ({from}) → Escalation Coder ({to}): {reason}", "warning", AgentIds.Coder,
            new { from = AgentIds.Coder, to = AgentIds.EscalationCoder, reason, handoverSummary = summary, attempt = number }, ct: ct);
        await S.EmitAsync("agent.escalation", $"Escalating {from} → {to}: {reason}", "warning", AgentIds.Coder,
            new { from_model = from, to_model = to, reason, attempt = number }, ct: ct);
    }

    private Task FinishAttemptAsync(string outcome, string? algorithm, CancellationToken ct) =>
        Attempts.FinishAsync(S, outcome, algorithm, ct);
}

internal static class Attempts
{
    public static async Task FinishAsync(PartSession s, string outcome, string? algorithm, CancellationToken ct)
    {
        if (s.CurrentAttempt is not { } attempt) return;
        await s.S.Store.Parts.UpsertAttemptAsync(attempt with { Outcome = outcome, FinishedAt = s.S.Clock.UtcNow }, ct);
        s.History.Add(new AttemptHistoryItem(attempt.Number, AgentIds.DisplayName(attempt.Agent), algorithm ?? attempt.Algorithm ?? "(no algorithm)", outcome));
        s.CurrentAttempt = null;
    }
}

/// <summary>System step: run the program on the verified examples in the sandbox.</summary>
public sealed class SandboxTestExecutor(PartSession s) : PartStep<CodeReady>("sandbox-test", s)
{
    public override async ValueTask HandleAsync(CodeReady code, IWorkflowContext context, CancellationToken ct = default)
    {
        S.PausePoint();
        using var span = Telemetry.StartStep("aps.sandbox_test", S.Year, S.Day, S.Part, code.Revision);
        span?.SetTag("aps.examples", S.Examples.Count);
        await S.SetActiveAgentAsync(AgentIds.System, ct);
        await S.SetStateAsync(PartState.TESTING, null, ct);
        if (S.Examples.Count == 0)
        {
            span.SetOutcome("no_examples", true);
            await context.SendMessageAsync(new TestsPassed(code.Revision, code.Code), ct);
            return;
        }
        await S.EmitAsync("test.started", $"Executing {S.Examples.Count} example test(s)", agent: AgentIds.System,
            data: new { revision = code.Revision, total = S.Examples.Count }, ct: ct);
        var outcome = await SandboxCalls.RunAsync(S, code.Code, [.. S.Examples.Select(e => e.Input)], S.Cfg.ExecTimeoutS, ct);

        var cases = new List<object>();
        var failures = new List<(int Index, string? Expected, string? Actual, string? Error)>();
        for (var i = 0; i < S.Examples.Count; i++)
        {
            var ex = S.Examples[i];
            var r = outcome.Results.FirstOrDefault(c => c.Index == i);
            var error = r?.Error ?? (i == 0 ? outcome.LoadError : null);
            var passed = r is not null && r.Error is null && (!ex.Reliable || (ex.Expected is not null && ExampleVerifier.OutputsMatch(ex.Expected, r.Output)));
            cases.Add(new { index = i, expected = ex.Expected, actual = r?.Output, passed, error, reliable = ex.Reliable });
            if (!passed) failures.Add((i, ex.Expected, r?.Output, error));
        }
        if (outcome.Status is ExecutionStatus.Timeout or ExecutionStatus.Error && outcome.Results.Count == 0 && failures.Count == 0)
            failures.Add((0, null, null, outcome.LoadError ?? Tail(outcome.Stderr) ?? outcome.Status.ToString()));

        if (failures.Count > 0)
        {
            var f = failures[0];
            string msg;
            if (outcome.TimedOut) { msg = $"Example tests timed out after {S.Cfg.ExecTimeoutS}s"; S.LastFailure = "timeout"; }
            else if (f.Error is not null) { msg = $"Test {f.Index + 1} raised {PartWorkflow.Short(f.Error.Split('\n')[0], 140)}"; S.LastFailure = "error"; }
            else { msg = $"Test failed: expected {f.Expected}, received {PartWorkflow.Short(f.Actual, 60)}"; S.LastFailure = "wrong"; }
            await S.EmitAsync("test.result", msg, "error", AgentIds.System,
                new { revision = code.Revision, passed = false, total = cases.Count, failures = failures.Take(5).Select(x => new { index = x.Index, expected = x.Expected, actual = x.Actual, error = x.Error }) }, ct: ct);
            S.Feedback.Add(Feedback(failures, outcome));
            await S.EmitAsync("agent.repair", "Analyzing failure", "warning", AgentIds.System, new { reason = msg }, ct: ct);
            await Attempts.FinishAsync(S, "tests_failed", null, ct);
            span.SetOutcome($"tests_failed:{S.LastFailure}", false);
            await context.SendMessageAsync(new CodeRequest(), ct);
            return;
        }
        var reliable = S.Examples.Count(e => e.Reliable);
        await S.EmitAsync("test.result",
            reliable > 0 ? $"All example tests passed ({reliable} with verified expected output)" : "Example runs completed without errors (no verified expected output)",
            reliable > 0 ? "success" : "warning", AgentIds.System, new { revision = code.Revision, passed = true, total = cases.Count, failures = Array.Empty<object>() }, ct: ct);
        if (reliable > 0) await S.SaveAsync(p => p with { TestsPassed = true }, ct);
        span.SetOutcome(reliable > 0 ? "passed" : "ran_unverified", true);
        await context.SendMessageAsync(new TestsPassed(code.Revision, code.Code), ct);
    }

    private string Feedback(List<(int Index, string? Expected, string? Actual, string? Error)> failures, ExecutionOutcome outcome)
    {
        var sb = new StringBuilder("Example tests failed:");
        foreach (var f in failures.Take(3))
        {
            var input = S.Examples[f.Index].Input;
            sb.Append($"\n- Example {f.Index + 1} input:\n{(input.Length < 800 ? input : input[..800] + "…")}");
            sb.Append(f.Error is not null ? $"\n  raised: {Tail(f.Error, 1200)}" : $"\n  expected: {f.Expected}\n  actual: {f.Actual}");
        }
        if (outcome.TimedOut) sb.Append("\nThe execution timed out; the algorithm is too slow.");
        if (outcome.Stdout.Length > 0) sb.Append("\nProgram output (tail):\n").Append(Tail(outcome.Stdout, 800));
        return sb.ToString();
    }

    internal static string? Tail(string? s, int n = 1500) => string.IsNullOrEmpty(s) ? null : s.Length <= n ? s : s[^n..];
}

/// <summary>System step: run on the real input, then the hardcode guard and the ledger check.</summary>
public sealed class RunInputExecutor(PartSession s) : PartStep<TestsPassed>("run-input", s)
{
    public override async ValueTask HandleAsync(TestsPassed tested, IWorkflowContext context, CancellationToken ct = default)
    {
        S.PausePoint();
        using var span = Telemetry.StartStep("aps.run_input", S.Year, S.Day, S.Part, tested.Revision);
        await S.SetStateAsync(PartState.EXECUTING_INPUT, null, ct);
        var timeout = S.ExtendedInputTimeout ? S.Cfg.ExecTimeoutMaxS : S.Cfg.ExecTimeoutS;
        span?.SetTag("aps.timeout_s", timeout);
        await S.EmitAsync("exec.started", "Executing actual input" + (S.ExtendedInputTimeout ? $" (extended timeout {timeout}s)" : ""),
            agent: AgentIds.System, data: new { revision = tested.Revision, timeout_s = timeout }, ct: ct);
        var outcome = await SandboxCalls.RunAsync(S, tested.Code, [S.Puzzle.InputText], timeout, ct);
        var r = outcome.Results.FirstOrDefault();
        var answer = r is { Error: null, Output: { } o } ? AnswerLedger.Normalize(o) : "";
        var error = r?.Error ?? outcome.LoadError ?? (r is null ? SandboxTestExecutor.Tail(outcome.Stderr) : null);

        if (outcome.TimedOut || answer.Length == 0)
        {
            string msg;
            if (outcome.TimedOut)
            {
                S.LastFailure = "timeout";
                S.ExtendedInputTimeout = true;
                msg = $"Real input timed out after {timeout}s — the algorithm is too slow for the full input";
            }
            else
            {
                S.LastFailure = "error";
                msg = $"Real input failed: {PartWorkflow.Short((error ?? "empty answer").Split('\n')[0])}";
            }
            await S.EmitAsync("exec.input", msg, "error", AgentIds.System,
                new { revision = tested.Revision, status = outcome.Status.ToString().ToLowerInvariant(), duration_ms = outcome.DurationMs }, ct: ct);
            S.Feedback.Add(msg + (error is not null ? $"\nError:\n{SandboxTestExecutor.Tail(error)}" : ""));
            await Attempts.FinishAsync(S, "exec_failed", null, ct);
            span.SetOutcome($"exec_failed:{S.LastFailure}", false);
            await context.SendMessageAsync(new CodeRequest(), ct);
            return;
        }
        await S.EmitAsync("exec.input", $"Candidate answer computed in {outcome.DurationMs / 1000.0:F1}s", "success", AgentIds.System,
            new { revision = tested.Revision, status = "ok", duration_ms = outcome.DurationMs, answer }, ct: ct);

        if (HardcodeGuard.LooksHardcoded(tested.Code, answer))
        {
            S.LastFailure = "hardcoded";
            const string msg = "The computed answer appears as a literal in the code; answers must be computed";
            await S.EmitAsync("agent.repair", msg, "warning", AgentIds.System, new { reason = msg }, ct: ct);
            S.Feedback.Add(msg + ".");
            await Attempts.FinishAsync(S, "rejected_hardcoded", null, ct);
            span.SetOutcome("rejected_hardcoded", false);
            await context.SendMessageAsync(new CodeRequest(), ct);
            return;
        }
        // An answer Advent of Code already rejected (or one outside a known too-high/too-low bound) is never
        // submitted again: it cannot be right, and repeated wrong answers earn ever longer lockouts.
        var ledger = await S.S.Gate.LedgerForAsync(S.Year, S.Day, S.Part, ct);
        var (ok, why) = ledger.Check(answer);
        if (!ok)
        {
            S.LastFailure = "wrong";
            await S.EmitAsync("submit.skipped_duplicate", $"Not submitting: {why}", "warning", AgentIds.System, new { reason = why }, ct: ct);
            S.Feedback.Add($"Your program produced {answer}, but {why}. The algorithm or parsing must be wrong.");
            await Attempts.FinishAsync(S, "rejected_duplicate", null, ct);
            span.SetOutcome("rejected_duplicate", false);
            await context.SendMessageAsync(new CodeRequest(), ct);
            return;
        }
        await S.SaveAsync(p => p with { CandidateAnswer = answer }, ct);
        await Attempts.FinishAsync(S, "candidate", null, ct);
        span.SetOutcome("candidate", true);
        await S.SetStateAsync(S.Cfg.ApprovalMode == ApprovalMode.Manual ? PartState.WAITING_FOR_APPROVAL : PartState.READY_TO_SUBMIT, null, ct);
        await context.SendMessageAsync(new CandidateReady(tested.Revision, tested.Code, answer), ct);
    }
}

internal static class SandboxCalls
{
    /// <summary>Runs code in a fresh sandbox session; sandbox outages pause the run without consuming an attempt.</summary>
    public static async Task<ExecutionOutcome> RunAsync(PartSession s, string code, IReadOnlyList<string> cases, int timeout, CancellationToken ct)
    {
        var sessionId = s.S.Sandbox.NewSessionId();
        using var span = Telemetry.Sandbox.StartActivity("aps.sandbox.execute", System.Diagnostics.ActivityKind.Client);
        span?.SetTag("aps.cases", cases.Count);
        span?.SetTag("aps.timeout_s", timeout);
        try
        {
            var outcome = await s.S.Sandbox.RunCasesAsync(code, cases, timeout, sessionId, ct);
            span?.SetTag("aps.sandbox.status", outcome.Status.ToString().ToLowerInvariant());
            span?.SetTag("aps.sandbox.duration_ms", outcome.DurationMs);
            span.SetOutcome(outcome.Status.ToString().ToLowerInvariant(), !outcome.TimedOut);
            return outcome;
        }
        catch (SandboxInfraException e)
        {
            span.SetOutcome("infra_error", false);
            await s.SaveAsync(p => p with { AttemptsUsed = Math.Max(0, p.AttemptsUsed - 1) }, ct);
            throw new PauseRunException($"sandbox unavailable: {e.Message}");
        }
        finally
        {
            await s.S.Sandbox.ReleaseAsync(sessionId, CancellationToken.None);
        }
    }
}
