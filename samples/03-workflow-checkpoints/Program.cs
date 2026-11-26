// Sample 03: the same kind of workflow as sample 02, now with MAF checkpoints. Kill the process, start it again: it
// continues after the last finished step. The models are not called again, and the pending approval comes back.
//
//   Analyst (GPT-6.1 Sol) → Coder (GPT-6.1 Sol) → Test (system step) → Approval (RequestPort, human) → Done
//                              ↑_________ fail __________|
//
// A workflow runs in supersteps; after each one a CheckpointManager saves the messages in flight, pending requests
// and executor state. ResumeStreamingAsync rebuilds the run from such a checkpoint.
//
// Needs FOUNDRY_PROJECT_ENDPOINT, set by `. .\scripts\env.ps1` in this shell (or from .env via a VS Code launch
// configuration):
//   . .\scripts\env.ps1
//   dotnet run --project samples/03-workflow-checkpoints                        # Ctrl+C at the approval prompt
//   dotnet run --project samples/03-workflow-checkpoints -- --resume            # asks again, no model calls
//   dotnet run --project samples/03-workflow-checkpoints -- --crash-after coder # dies right after the Coder's step
//
// Checkpoints go to .checkpoints/sample-03 with the framework's FileSystemJsonCheckpointStore. The solver uses the same
// API with a Cosmos DB store (src/Aps.Agent/Persistence/CheckpointStores.cs), because a hosted session's disk does
// not survive a recycle.
using System.Data;
using Azure.AI.Projects;
using Azure.Identity;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;

static string Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v
    : throw new InvalidOperationException($"{name} is not set. Run '. .\\scripts\\env.ps1' in this terminal first.");

string? Arg(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
var resume = args.Contains("--resume");
var crashAfter = Arg("--crash-after");

// ---- the checkpoint store: a directory, one session per run
var dir = new DirectoryInfo(Path.Combine(Environment.CurrentDirectory, ".checkpoints", "sample-03"));
var sessionFile = Path.Combine(dir.FullName, "..", "sample-03.session");
if (!resume && dir.Exists) dir.Delete(recursive: true);
dir.Create();
var sessionId = resume ? File.ReadAllText(sessionFile).Trim() : $"lantern-{DateTime.Now:HHmmss}";
if (!resume) File.WriteAllText(sessionFile, sessionId);

// The CheckpointManager serializes checkpoints as JSON into the store; checkpoints are grouped per session id.
using var store = new FileSystemJsonCheckpointStore(dir);
var checkpoints = CheckpointManager.CreateJson(store);

// ---- agents and steps
var credential = new DefaultAzureCredential();
var project = new AIProjectClient(new Uri(Env("FOUNDRY_PROJECT_ENDPOINT")), credential);
var model = Environment.GetEnvironmentVariable("WORKHORSE_DEPLOYMENT") ?? "gpt-6.1-sol";
AIAgent analyst = project.AsAIAgent(model: model, name: "analyst",
    instructions: "Explain in ONE short sentence how to compute the answer. Do not compute it.");
AIAgent coder = project.AsAIAgent(model: model, name: "coder",
    instructions: "Reply with ONE arithmetic expression (digits, + - * / and parentheses only) whose value answers the question. No text.");

var stats = new Stats();
var analystStep = new AnalystStep(analyst, stats);
var coderStep = new CoderStep(coder, stats);
var testStep = new TestStep(Expected: 8);
var approval = RequestPort.Create<string, bool>("approval");
var done = new DoneStep();

var workflow = new WorkflowBuilder(analystStep)
    .AddEdge(analystStep, coderStep)
    .AddEdge(coderStep, testStep)
    .AddEdge<object>(testStep, coderStep, m => m is Plan)     // failed test → back to the Coder
    .AddEdge<object>(testStep, approval, m => m is string)    // candidate answer → human approval
    .AddEdge(approval, done)
    .WithOutputFrom(done)
    .Build();

// ---- run, or resume from the latest checkpoint
// RunStreamingAsync starts a new run that checkpoints under sessionId; ResumeStreamingAsync restores the latest
// checkpoint into the same workflow definition and continues from there without the original input.
StreamingRun run;
if (resume)
{
    var latest = await checkpoints.GetLatestCheckpointAsync(sessionId)
                 ?? throw new InvalidOperationException($"no checkpoint for session {sessionId}");
    Console.WriteLine($"♻️  resuming session {sessionId} from checkpoint {latest.CheckpointId[..8]}");
    run = await InProcessExecution.ResumeStreamingAsync(workflow, latest, checkpoints);
}
else
{
    Console.WriteLine($"▶️  new session {sessionId}");
    run = await InProcessExecution.RunStreamingAsync(workflow,
        new Puzzle("Lantern drift: lines (3,5), (10,4), (7,7). What is the total absolute difference per line, summed?"),
        checkpoints, sessionId);
}

await using (run)
{
    var ran = new List<string>();
    var inStep = false;
    RequestInfoEvent? pending = null;

    // Ask the human only once the superstep that raised the request is checkpointed: the request is then in the
    // checkpoint, so killing the process at the prompt loses nothing.
    async Task AskAsync(RequestInfoEvent info)
    {
        info.Request.TryGetDataAs<string>(out var answer);
        Console.Write($"\n🙋 Approve answer {answer}? [y/n] (or Ctrl+C to kill the process) ");
        // Trim a byte-order mark too: PowerShell adds one when input is piped.
        var yes = (Console.ReadLine() ?? "").Trim('﻿', ' ', '\t').StartsWith("y", StringComparison.OrdinalIgnoreCase);
        await run.SendResponseAsync(info.Request.CreateResponse(yes));
    }

    await foreach (var evt in run.WatchStreamAsync())
    {
        switch (evt)
        {
            case SuperStepStartedEvent:
                inStep = true;
                break;
            case ExecutorCompletedEvent step when step.ExecutorId is { } id:
                ran.Add(id);
                break;
            // A superstep is one round in which all executors with pending messages run; when it completes, the
            // checkpoint for that round has been written.
            case SuperStepCompletedEvent { CompletionInfo.Checkpoint: { } checkpoint } superstep:
                inStep = false;
                var waiting = pending is not null ? " (approval request pending)" : "";
                Console.WriteLine($"   💾 checkpoint {superstep.StepNumber} after {string.Join(", ", ran)}{waiting}  ({checkpoint.CheckpointId[..8]})");
                if (crashAfter is not null && ran.Contains(crashAfter))
                {
                    Console.WriteLine($"\n💥 process killed after '{crashAfter}'. Run again with --resume.");
                    Environment.Exit(1);
                }
                ran.Clear();
                if (pending is { } request) { pending = null; await AskAsync(request); }
                break;
            case RequestInfoEvent info:
                // Raised inside a superstep: wait for its checkpoint. Re-raised on resume (outside a superstep): ask now.
                if (inStep) pending = info;
                else await AskAsync(info);
                break;
            case WorkflowOutputEvent output:
                Console.WriteLine($"\n✅ {output.Data}   (model calls in this session: {stats.ModelCalls})");
                return;
            case WorkflowErrorEvent error:
                Console.WriteLine($"\n❌ {error.Data}");
                return;
        }
    }
}
Console.WriteLine("Nothing left to do: this session already finished.");

record Puzzle(string Question);
record Plan(string Question, string Approach, string? Feedback = null);
record Candidate(string Expression);

/// <summary>State that must survive a restart. It travels inside every checkpoint.</summary>
sealed class Stats { public int ModelCalls { get; set; } }

/// <summary>Agent step. As the start executor it also saves and restores the shared state with each checkpoint.</summary>
sealed class AnalystStep(AIAgent agent, Stats stats) : Executor<Puzzle, Plan>("analyst")
{
    public override async ValueTask<Plan> HandleAsync(Puzzle puzzle, IWorkflowContext context, CancellationToken ct = default)
    {
        var approach = (await agent.RunAsync(puzzle.Question, cancellationToken: ct)).Text.Trim();
        stats.ModelCalls++;
        Console.WriteLine($"🧠 analyst: {approach}");
        return new Plan(puzzle.Question, approach);
    }

    // Called before each checkpoint is saved: queue the state that must go into it.
    protected override ValueTask OnCheckpointingAsync(IWorkflowContext context, CancellationToken ct = default) =>
        context.QueueStateUpdateAsync("model-calls", stats.ModelCalls, ct);

    // Called after a resume restored a checkpoint: read the state back into this process.
    protected override async ValueTask OnCheckpointRestoredAsync(IWorkflowContext context, CancellationToken ct = default)
    {
        stats.ModelCalls = await context.ReadStateAsync<int>("model-calls", ct);
        Console.WriteLine($"   ♻️  state restored from the checkpoint: {stats.ModelCalls} model call(s) so far");
    }
}

/// <summary>Agent step: turns the plan into an expression.</summary>
sealed class CoderStep(AIAgent agent, Stats stats) : Executor<Plan, Candidate>("coder")
{
    public override async ValueTask<Candidate> HandleAsync(Plan plan, IWorkflowContext context, CancellationToken ct = default)
    {
        var prompt = $"{plan.Question}\nApproach: {plan.Approach}" + (plan.Feedback is { } f ? $"\nPrevious attempt failed: {f}" : "");
        var expression = (await agent.RunAsync(prompt, cancellationToken: ct)).Text.Trim();
        stats.ModelCalls++;
        Console.WriteLine($"🧑‍💻 coder: {expression}");
        return new Candidate(expression);
    }
}

/// <summary>System step: evaluates the expression and checks it against the known answer.</summary>
sealed class TestStep(int Expected) : Executor<Candidate>("test")
{
    protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder protocolBuilder) =>
        base.ConfigureProtocol(protocolBuilder).SendsMessage<Plan>().SendsMessage<string>();

    public override async ValueTask HandleAsync(Candidate candidate, IWorkflowContext context, CancellationToken ct = default)
    {
        string? value = null;
        try { value = Convert.ToString(new DataTable().Compute(candidate.Expression, null)); } catch (Exception) { }
        var ok = value == Expected.ToString();
        Console.WriteLine($"🧪 test: {candidate.Expression} = {value ?? "error"} → {(ok ? "pass" : "fail")}");
        if (ok) await context.SendMessageAsync(value!, ct);
        else await context.SendMessageAsync(new Plan("Lantern drift: lines (3,5), (10,4), (7,7). Total absolute difference?",
            "Sum |a-b| per line.", $"'{candidate.Expression}' evaluated to {value ?? "an error"}"), ct);
    }
}

/// <summary>Final step after the human decision.</summary>
sealed class DoneStep() : Executor<bool, string>("done")
{
    public override ValueTask<string> HandleAsync(bool approved, IWorkflowContext context, CancellationToken ct = default) =>
        ValueTask.FromResult(approved ? "Approved: the answer would now be submitted." : "Rejected by the operator.");
}
