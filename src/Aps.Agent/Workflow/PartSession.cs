using System.Text.Json;
using Aps.Agent.Abstractions;
using Aps.Agent.Agents;
using Aps.Agent.Aoc;
using Aps.Agent.Domain;
using Aps.Agent.Execution;
using Aps.Agent.Persistence;
using Aps.Agent.Verification;

namespace Aps.Agent.Workflow;

/// <summary>The operator paused or stopped the run, or an infrastructure failure requires a pause.</summary>
public sealed class PauseRunException(string reason, bool stop = false) : Exception(reason)
{
    public bool Stop { get; } = stop;
}

/// <summary>The configured spend limit was reached.</summary>
public sealed class BudgetExhaustedException(string reason) : Exception(reason);

/// <summary>
/// Puzzle material for one part. The personal input never reaches a model: Advent of Code asks that inputs are not
/// shared, so prompts only describe its shape.
/// </summary>
public sealed record PuzzleMaterial(
    string Title,
    string PuzzleText,
    IReadOnlyList<string> PreBlocks,
    IReadOnlyList<string> Emphasized,
    string ArticleText,
    string InputText,
    string? Part1Summary = null,
    string? Part1Answer = null,
    string? Part1Code = null);

/// <summary>A part's working memory as stored in its workflow checkpoints. The puzzle input is never part of it.</summary>
public sealed record PartMemory(
    Analysis? Analysis,
    IReadOnlyList<VerifiedExample> Examples,
    IReadOnlyList<string> Feedback,
    IReadOnlyList<AttemptHistoryItem> History,
    string? PreviousCode,
    string? LastFailure,
    bool AfterRejection,
    bool ExtendedInputTimeout,
    string? HandoverSummary,
    Attempt? CurrentAttempt);

/// <summary>Services a part workflow needs. One instance per run.</summary>
public sealed record SolverServices(
    IApsStore Store,
    EventLog Events,
    RunLease Lease,
    AgentRunner Runner,
    IAgentTeam Team,
    ICodeExecutor Sandbox,
    AocClient Aoc,
    SubmissionGate Gate,
    RunControl Control,
    IClock Clock);

/// <summary>
/// Working memory and helpers for one part. Every executor of the part workflow shares one instance; all side effects
/// go through it, so every step is persisted and emitted as an event.
/// </summary>
public sealed class PartSession(SolverServices services, Run run, PartProgress progress, PuzzleMaterial puzzle) : IAgentCallSink
{
    public SolverServices S { get; } = services;
    public Run Run { get; private set; } = run;
    public RunConfig Cfg => Run.Config;
    public PartProgress Progress { get; private set; } = progress;
    public PuzzleMaterial Puzzle { get; } = puzzle;
    public int Year => Run.Year;
    public int Day => Progress.Day;
    public int Part => Progress.Part;

    // ---- working memory: saved in every workflow checkpoint (PartMemory), restored when the part resumes from one
    public Analysis? Analysis { get; set; }
    public List<VerifiedExample> Examples { get; } = [];
    public List<string> Feedback { get; } = [];
    public List<AttemptHistoryItem> History { get; } = [];
    public string? PreviousCode { get; set; }
    public string? LastFailure { get; set; }
    public bool AfterRejection { get; set; }
    public bool ExtendedInputTimeout { get; set; }
    public string? HandoverSummary { get; set; }
    public Attempt? CurrentAttempt { get; set; }

    /// <summary>The working memory as it goes into a workflow checkpoint.</summary>
    public PartMemory Snapshot() => new(Analysis, [.. Examples], [.. Feedback], [.. History], PreviousCode, LastFailure, AfterRejection,
        ExtendedInputTimeout, HandoverSummary, CurrentAttempt);

    /// <summary>Restores the working memory from a workflow checkpoint.</summary>
    public void Restore(PartMemory m)
    {
        Analysis = m.Analysis;
        Examples.Clear(); Examples.AddRange(m.Examples);
        Feedback.Clear(); Feedback.AddRange(m.Feedback);
        History.Clear(); History.AddRange(m.History);
        PreviousCode = m.PreviousCode;
        LastFailure = m.LastFailure;
        AfterRejection = m.AfterRejection;
        ExtendedInputTimeout = m.ExtendedInputTimeout;
        HandoverSummary = m.HandoverSummary;
        CurrentAttempt = m.CurrentAttempt;
    }

    public string ModelDisplay(string agentId) => S.Team.ModelFor(agentId).DisplayName;

    public PromptContext Prompt()
    {
        var (preview, stats) = Prompts.InputShape(Puzzle.InputText);
        return new PromptContext
        {
            Year = Year, Day = Day, Part = Part, Title = Puzzle.Title, PuzzleText = Puzzle.PuzzleText,
            InputPreview = preview, InputStats = stats,
            Part1Summary = Puzzle.Part1Summary, Part1Answer = Puzzle.Part1Answer, Part1Code = Puzzle.Part1Code,
            Analysis = Analysis, PreviousCode = PreviousCode, Feedback = [.. Feedback], History = [.. History],
            HandoverSummary = HandoverSummary,
        };
    }

    public Task<DomainEvent> EmitAsync(string type, string message, string level = "info", string? agent = null, object? data = null,
        JsonElement? patch = null, CancellationToken ct = default) =>
        S.Events.AppendAsync(Run.Id, new NewEvent(type, message, level, Day, Part, agent, data, patch), ct);

    public async Task SetActiveAgentAsync(string agentId, CancellationToken ct)
    {
        if (Progress.ActiveAgent == agentId) return;
        await SaveAsync(p => p with { ActiveAgent = agentId }, ct);
        Run = await S.Lease.UpdateRunAsync(Run.Id, r => r with { ActiveAgent = agentId }, ct);
        var model = agentId == AgentIds.System ? null : ModelDisplay(agentId);
        await EmitAsync("agent.active", agentId == AgentIds.System ? "System step" : $"{AgentIds.DisplayName(agentId)} ({model}) is working",
            agent: agentId, data: new { agent = agentId, model },
            patch: Patch(("replace", "/activeAgent", agentId)), ct: ct);
    }

    public async Task SetStateAsync(PartState state, string? reason, CancellationToken ct)
    {
        if (Progress.State == state) return;
        var from = Progress.State;
        await SaveAsync(p => p with { State = state, StatusReason = reason ?? p.StatusReason }, ct);
        await EmitAsync("part.state", $"{from} → {state}", data: new { from = from.ToString(), to = state.ToString(), reason },
            patch: Patch(("replace", $"/calendar/{Day - 1}/{Part - 1}", state.ToString())), ct: ct);
    }

    public async Task SaveAsync(Func<PartProgress, PartProgress> mutate, CancellationToken ct)
    {
        Progress = mutate(Progress) with { UpdatedAt = S.Clock.UtcNow };
        await S.Store.Parts.UpsertPartAsync(Progress, ct);
    }

    /// <summary>Cooperative pause/stop point between steps.</summary>
    public void PausePoint() => S.Control.ThrowIfPauseRequested();

    // ---- IAgentCallSink: every model call is a pause point and is checked against the spend limit first
    public async Task OnStartedAsync(string agentId, ModelInfo model, string purpose, CancellationToken ct)
    {
        PausePoint();
        var current = await S.Store.Runs.GetAsync(Run.Id, ct) ?? Run;
        if (Cfg.MaxSpendUsd > 0 && current.Usage.Totals.CostUsd >= Cfg.MaxSpendUsd)
            throw new BudgetExhaustedException($"spend limit reached (${current.Usage.Totals.CostUsd:F2} of ${Cfg.MaxSpendUsd:F2})");
        await EmitAsync("model.call.started", $"Calling {model.DisplayName}", agent: agentId,
            data: new { model_key = model.Key, deployment = model.Deployment, purpose }, ct: ct);
    }

    public async Task OnFinishedAsync(string agentId, ModelInfo model, string purpose, UsageTotals usage, string? error, CancellationToken ct)
    {
        await S.Store.Parts.AddInvocationAsync(new ModelInvocation
        {
            Id = $"inv-{Guid.NewGuid():n}", RunId = Run.Id, Day = Day, Part = Part, Agent = agentId, ModelKey = model.Key,
            Deployment = model.Deployment, Purpose = purpose, InputTokens = usage.InputTokens, OutputTokens = usage.OutputTokens,
            ReasoningTokens = usage.ReasoningTokens, LatencyMs = usage.LatencyMs, CostUsd = usage.CostUsd,
            Status = error is null ? "ok" : "error", Error = error, CreatedAt = S.Clock.UtcNow,
        }, ct);
        Run = await S.Lease.UpdateRunAsync(Run.Id, r => r with { Usage = r.Usage.Add(agentId, model.Key, usage) }, ct);
        var u = Run.Usage;
        await EmitAsync("model.call.finished", error is null
                ? $"{model.DisplayName} responded in {usage.LatencyMs / 1000.0:F1}s"
                : $"{model.DisplayName} failed: {error}",
            error is null ? "info" : "warning", agentId,
            new
            {
                model_key = model.Key, input_tokens = usage.InputTokens, output_tokens = usage.OutputTokens,
                reasoning_tokens = usage.ReasoningTokens, latency_ms = usage.LatencyMs, cost_usd = usage.CostUsd,
                run_cost_usd = u.Totals.CostUsd,
            },
            Patch(("replace", "/usage", u)), ct);
        if (Cfg.MaxSpendUsd > 0 && u.Totals.CostUsd >= 0.8 * Cfg.MaxSpendUsd)
            await EmitAsync("budget.warning", $"Spent ${u.Totals.CostUsd:F2} of ${Cfg.MaxSpendUsd:F2} budget", "warning",
                data: new { spent_usd = u.Totals.CostUsd, limit_usd = Cfg.MaxSpendUsd }, ct: ct);
    }

    public static JsonElement Patch(params (string Op, string Path, object? Value)[] ops) =>
        JsonSerializer.SerializeToElement(ops.Select(o => new { op = o.Op, path = o.Path, value = o.Value }).ToArray(), PatchJson);

    private static readonly JsonSerializerOptions PatchJson = new(JsonSerializerDefaults.Web);
}

/// <summary>
/// Cooperative pause/stop requests for a run. Steps check it between side effects instead of being cancelled midway,
/// so a pause never interrupts a submission or leaves half-written state.
/// </summary>
public sealed class RunControl
{
    private volatile string? _pauseReason;
    private volatile bool _stop;

    public void RequestPause(string reason = "paused by operator") => _pauseReason = reason;

    public void RequestStop() { _stop = true; _pauseReason = "stopped by operator"; }

    public void ThrowIfPauseRequested()
    {
        if (_pauseReason is { } reason) throw new PauseRunException(reason, _stop);
    }

    /// <summary>Delay that wakes early when a pause is requested.</summary>
    public async Task SleepAsync(IClock clock, TimeSpan delay, CancellationToken ct)
    {
        var end = clock.UtcNow + delay;
        while (clock.UtcNow < end)
        {
            ThrowIfPauseRequested();
            var step = end - clock.UtcNow;
            await clock.DelayAsync(step < TimeSpan.FromSeconds(1) ? step : TimeSpan.FromSeconds(1), ct);
        }
    }
}
