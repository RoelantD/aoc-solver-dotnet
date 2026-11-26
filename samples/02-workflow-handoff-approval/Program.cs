// Sample 02: a MAF workflow with two agents, a deterministic test step, a handoff and a human approval.
//
//   Coder (GPT-6.1 Sol) → Test (system step) ─pass→ Approval (RequestPort, human) → Done
//                 ↑           └─fail (2x)→ handoff → Escalation Coder (Claude Opus 5.5) → Test …
//
// A workflow is a graph of executors (steps) connected by typed edges. Agent steps call a model; the test step is
// plain code, so the decision "is this answer right?" never depends on a model. Two failures hand the work from GPT
// to Claude, and a RequestPort pauses the workflow until a human approves the answer.
//
// Runs entirely in-process; the "test" evaluates the program locally ONLY because this sample's code is a pure
// arithmetic expression. The real solver never runs generated code outside the sandbox.
//
// Needs FOUNDRY_PROJECT_ENDPOINT and FOUNDRY_RESOURCE_NAME, set by `. .\scripts\env.ps1` in this shell
// (or from .env via a VS Code launch configuration):
//   . .\scripts\env.ps1
//   dotnet run --project samples/02-workflow-handoff-approval
//   dotnet run --project samples/02-workflow-handoff-approval -- --simulate-failures 2   # show the handoff on demand
//
// --simulate-failures N makes the test step reject the first N candidates (printed as "simulated"): GPT-6.1 Sol usually
// solves this small puzzle at once, so without it the handoff to Claude rarely happens live.
using System.Data;
using Anthropic;
using Anthropic.Foundry;
using Azure.AI.Projects;
using Azure.Identity;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;

static string Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v
    : throw new InvalidOperationException($"{name} is not set. Run '. .\\scripts\\env.ps1' in this terminal first.");

var credential = new DefaultAzureCredential();
const string Instructions = "Reply with ONE arithmetic expression (digits, + - * / and parentheses only) whose value answers the question. No text.";

// Two providers behind the same AIAgent abstraction: GPT via the Foundry project, Claude via Anthropic on Foundry.
AIAgent coder = new AIProjectClient(new Uri(Env("FOUNDRY_PROJECT_ENDPOINT")), credential)
    .AsAIAgent(model: Environment.GetEnvironmentVariable("WORKHORSE_DEPLOYMENT") ?? "gpt-6.1-sol", name: "coder", instructions: Instructions);
AIAgent escalation = new AnthropicFoundryClient(new AnthropicFoundryIdentityTokenCredentials(credential,
        Env("FOUNDRY_RESOURCE_NAME"), ["https://ai.azure.com/.default"]))
    .AsAIAgent(model: Environment.GetEnvironmentVariable("ESCALATION_DEPLOYMENT") ?? "claude-opus-5-5", name: "escalation-coder", instructions: Instructions);

var simulated = Array.IndexOf(args, "--simulate-failures") is var at and >= 0 && at + 1 < args.Length && int.TryParse(args[at + 1], out var n) ? n : 0;
var task = new Task("Lantern drift: lines (3,5), (10,4), (7,7). Total absolute difference?", Expected: 8, FailuresBeforeHandoff: 2);
var coderStep = new CoderStep(coder, escalation, task);
var testStep = new TestStep(task, simulated);
// A RequestPort is a step handled outside the workflow: a string sent to it surfaces as a RequestInfoEvent, and the
// bool the caller sends back continues the workflow from there.
var approval = RequestPort.Create<string, bool>("approval");
var done = new DoneStep();

// WorkflowBuilder starts at coderStep. Each edge routes an executor's output to the next one; a conditional edge
// only fires when its predicate matches the message. WithOutputFrom marks which executor yields the workflow result.
var workflow = new WorkflowBuilder(coderStep)
    .AddEdge(coderStep, testStep)
    .AddEdge<object>(testStep, coderStep, m => m is Retry)       // repair loop (and handoff after N failures)
    .AddEdge<object>(testStep, approval, m => m is string)        // candidate answer → human approval
    .AddEdge(approval, done)
    .WithOutputFrom(done)
    .Build();

// RunStreamingAsync starts the workflow with its first input; WatchStreamAsync yields events as the steps run.
await using var run = await InProcessExecution.RunStreamingAsync(workflow, new Retry(null));
await foreach (var evt in run.WatchStreamAsync())
{
    switch (evt)
    {
        // The workflow is paused on the RequestPort: ask the human and send the answer back as the response.
        case RequestInfoEvent info when info.Request.TryGetDataAs<string>(out var answer):
            Console.Write($"\n🙋 Approve answer {answer}? [y/n] ");
            // Trim a byte-order mark too: PowerShell adds one when input is piped.
            var yes = (Console.ReadLine() ?? "").Trim('﻿', ' ', '\t').StartsWith("y", StringComparison.OrdinalIgnoreCase);
            await run.SendResponseAsync(info.Request.CreateResponse(yes));
            break;
        case WorkflowOutputEvent output:
            Console.WriteLine($"\n✅ {output.Data}");
            return;
        case WorkflowErrorEvent error:
            Console.WriteLine($"\n❌ {error.Data}");
            return;
    }
}

record Task(string Question, int Expected, int FailuresBeforeHandoff);
record Retry(string? Feedback);
record Candidate(string Expression);

/// <summary>
/// Agent step: the Coder writes an expression; after N failures the work is handed to the Escalation Coder.
/// An Executor&lt;TIn, TOut&gt; handles one message type; the value it returns is sent along its outgoing edges.
/// </summary>
sealed class CoderStep(AIAgent coder, AIAgent escalation, Task task) : Executor<Retry, Candidate>("coder")
{
    private int _failures;

    public override async ValueTask<Candidate> HandleAsync(Retry retry, IWorkflowContext context, CancellationToken ct = default)
    {
        if (retry.Feedback is not null) _failures++;
        var agent = _failures >= task.FailuresBeforeHandoff ? escalation : coder;
        if (_failures == task.FailuresBeforeHandoff) Console.WriteLine($"\n🤝 handoff: coder → escalation-coder ({_failures} failed attempts)");
        var prompt = task.Question + (retry.Feedback is { } f ? $"\nPrevious attempt failed: {f}" : "");
        var reply = (await agent.RunAsync(prompt, cancellationToken: ct)).Text.Trim();
        Console.WriteLine($"\n🧑‍💻 {agent.Name}: {reply}");
        return new Candidate(reply);
    }
}

/// <summary>System step: deterministic test against the known example answer.</summary>
sealed class TestStep(Task task, int simulatedFailures) : Executor<Candidate>("test")
{
    private int _runs;

    // This step sends messages itself instead of returning one, so it declares both types it may send for routing.
    protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder protocolBuilder) =>
        base.ConfigureProtocol(protocolBuilder).SendsMessage<Retry>().SendsMessage<string>();

    public override async ValueTask HandleAsync(Candidate candidate, IWorkflowContext context, CancellationToken ct = default)
    {
        string? value = null;
        try { value = Convert.ToString(new DataTable().Compute(candidate.Expression, null)); } catch (Exception) { }
        var simulate = ++_runs <= simulatedFailures;
        var ok = value == task.Expected.ToString() && !simulate;
        Console.WriteLine($"🧪 test: {candidate.Expression} = {value ?? "error"} → {(ok ? "pass" : simulate ? "fail (simulated)" : "fail")}");
        var feedback = simulate
            ? $"'{candidate.Expression}' was rejected by the test; write the expression differently."
            : $"'{candidate.Expression}' evaluated to {value ?? "an error"}";
        await context.SendMessageAsync(ok ? value! : new Retry(feedback), ct);
    }
}

/// <summary>Final step after the human decision.</summary>
sealed class DoneStep() : Executor<bool, string>("done")
{
    public override ValueTask<string> HandleAsync(bool approved, IWorkflowContext context, CancellationToken ct = default) =>
        ValueTask.FromResult(approved ? "Approved: the answer would now be submitted." : "Rejected by the operator.");
}
