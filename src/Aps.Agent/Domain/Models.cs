using System.Text.Json;
using System.Text.Json.Serialization;
using Aps.Agent.Aoc;

namespace Aps.Agent.Domain;

// Cosmos DB documents and the shared vocabulary. Enum wire names are what the dashboard expects.

[JsonConverter(typeof(JsonStringEnumConverter<RunStatus>))]
public enum RunStatus
{
    [JsonStringEnumMemberName("created")] Created,
    [JsonStringEnumMemberName("running")] Running,
    [JsonStringEnumMemberName("paused")] Paused,
    [JsonStringEnumMemberName("stopped")] Stopped,
    [JsonStringEnumMemberName("completed")] Completed,
    [JsonStringEnumMemberName("budget_exhausted")] BudgetExhausted,
    [JsonStringEnumMemberName("failed")] Failed,
}

[JsonConverter(typeof(JsonStringEnumConverter<RunMode>))]
public enum RunMode
{
    [JsonStringEnumMemberName("live")] Live,
    [JsonStringEnumMemberName("replay")] Replay,
}

[JsonConverter(typeof(JsonStringEnumConverter<ApprovalMode>))]
public enum ApprovalMode
{
    [JsonStringEnumMemberName("auto")] Auto,
    [JsonStringEnumMemberName("manual")] Manual,
}

[JsonConverter(typeof(JsonStringEnumConverter<PartState>))]
public enum PartState
{
    NOT_STARTED, FETCHING, ANALYZING, GENERATING, TESTING, REPAIRING, ESCALATING, EXECUTING_INPUT,
    READY_TO_SUBMIT, WAITING_FOR_APPROVAL, SUBMITTING, WAITING_FOR_COOLDOWN, VERIFIED, BLOCKED, FAILED, SKIPPED,
}

[JsonConverter(typeof(JsonStringEnumConverter<ApprovalState>))]
public enum ApprovalState
{
    [JsonStringEnumMemberName("pending")] Pending,
    [JsonStringEnumMemberName("approved")] Approved,
    [JsonStringEnumMemberName("rejected")] Rejected,
}

/// <summary>Agent ids. "system" marks deterministic steps.</summary>
public static class AgentIds
{
    public const string Analyst = "analyst";
    public const string Coder = "coder";
    public const string EscalationCoder = "escalation-coder";
    public const string System = "system";

    public static string DisplayName(string id) => id switch
    {
        Analyst => "Analyst",
        Coder => "Coder",
        EscalationCoder => "Escalation Coder",
        _ => "System",
    };
}

public static class PartStates
{
    public static readonly IReadOnlySet<PartState> Terminal =
        new HashSet<PartState> { PartState.VERIFIED, PartState.BLOCKED, PartState.FAILED, PartState.SKIPPED };

    public static readonly IReadOnlySet<SubmissionOutcome> Rejected =
        new HashSet<SubmissionOutcome> { SubmissionOutcome.Incorrect, SubmissionOutcome.TooHigh, SubmissionOutcome.TooLow };
}

/// <summary>Run limits and behaviour.</summary>
public sealed record RunConfig
{
    public int MaxAttemptsPerPart { get; init; } = 6;
    public int EscalateAfterFailures { get; init; } = 3;
    public bool EscalateOnTimeout { get; init; } = true;
    public int MaxSubmissionsPerPart { get; init; } = 3;
    public int ExecTimeoutS { get; init; } = 30;
    public int ExecTimeoutMaxS { get; init; } = 120;
    public double MaxSpendUsd { get; init; } = 20.0;
    public double AocMinIntervalS { get; init; } = 5.0;
    public ApprovalMode ApprovalMode { get; init; } = ApprovalMode.Auto;
    public int MaxOutputTokens { get; init; } = 32000;

    /// <summary>Clamps client-supplied limits into safe bounds; returns the validated copy.</summary>
    public RunConfig Validated() => this with
    {
        MaxAttemptsPerPart = Math.Clamp(MaxAttemptsPerPart, 1, 20),
        EscalateAfterFailures = Math.Clamp(EscalateAfterFailures, 0, 20),
        MaxSubmissionsPerPart = Math.Clamp(MaxSubmissionsPerPart, 1, 10),
        ExecTimeoutMaxS = Math.Clamp(ExecTimeoutMaxS, 5, 220),
        ExecTimeoutS = Math.Clamp(ExecTimeoutS, 5, Math.Clamp(ExecTimeoutMaxS, 5, 220)),
        MaxSpendUsd = Math.Max(0, MaxSpendUsd),
        AocMinIntervalS = Math.Clamp(AocMinIntervalS, 1.0, 120.0),
        MaxOutputTokens = Math.Clamp(MaxOutputTokens, 1024, 128000),
    };
}

public sealed record Lease(string Owner, DateTimeOffset AcquiredAt, DateTimeOffset ExpiresAt);

/// <summary>
/// A pending operator decision on a candidate answer. <see cref="RequestId"/> is the workflow's RequestInfoEvent
/// id, so a resumed workflow that asks again finds the same request (and any decision already made). Applied exactly once.
/// </summary>
public sealed record ApprovalRequest
{
    public required string RequestId { get; init; }
    public required int Day { get; init; }
    public required int Part { get; init; }
    public required string Answer { get; init; }
    public ApprovalState State { get; init; } = ApprovalState.Pending;
    public DateTimeOffset? DecidedAt { get; init; }
    public string? DecidedBy { get; init; }
    public DateTimeOffset? AppliedAt { get; init; }
}

public sealed record UsageTotals
{
    public int Calls { get; init; }
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
    public long ReasoningTokens { get; init; }
    public long LatencyMs { get; init; }
    public double CostUsd { get; init; }

    public UsageTotals Add(UsageTotals other) => new()
    {
        Calls = Calls + other.Calls,
        InputTokens = InputTokens + other.InputTokens,
        OutputTokens = OutputTokens + other.OutputTokens,
        ReasoningTokens = ReasoningTokens + other.ReasoningTokens,
        LatencyMs = LatencyMs + other.LatencyMs,
        CostUsd = CostUsd + other.CostUsd,
    };
}

public sealed record RunUsage
{
    public Dictionary<string, UsageTotals> ByAgent { get; init; } = [];
    public Dictionary<string, UsageTotals> ByModel { get; init; } = [];
    public UsageTotals Totals { get; init; } = new();

    public RunUsage Add(string agent, string model, UsageTotals usage)
    {
        var byAgent = new Dictionary<string, UsageTotals>(ByAgent);
        byAgent[agent] = byAgent.GetValueOrDefault(agent, new()).Add(usage);
        var byModel = new Dictionary<string, UsageTotals>(ByModel);
        byModel[model] = byModel.GetValueOrDefault(model, new()).Add(usage);
        return new() { ByAgent = byAgent, ByModel = byModel, Totals = Totals.Add(usage) };
    }
}

/// <summary>Run document (container <c>runs</c>, pk <c>/id</c>).</summary>
public sealed record Run
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    public RunMode Mode { get; init; } = RunMode.Live;
    public required int Year { get; init; }
    public required int DayFrom { get; init; }
    public required int DayTo { get; init; }
    public RunStatus Status { get; init; } = RunStatus.Created;
    public string? StopReason { get; init; }
    public bool AutoSubmit { get; init; }
    public DateTimeOffset? AuthorizedAt { get; init; }
    public RunConfig Config { get; init; } = new();
    public int? CurrentDay { get; init; }
    public int? CurrentPart { get; init; }
    public string? ActiveAgent { get; init; }
    public Lease? Lease { get; init; }
    public long LastSeq { get; init; }

    /// <summary>"pause" or "stop" requested by any instance; the leaseholder applies it.</summary>
    public string? ControlRequest { get; init; }

    public List<ApprovalRequest> PendingApprovals { get; init; } = [];
    public RunUsage Usage { get; init; } = new();
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }

    /// <summary>
    /// Optimistic-concurrency token from the store; never serialized into the document body. Writes are conditioned
    /// on it, so two instances updating the same run cannot silently overwrite each other (the basis of the lease).
    /// </summary>
    [JsonIgnore] public string? ETag { get; init; }

    [JsonIgnore]
    public bool IsTerminal => Status is RunStatus.Stopped or RunStatus.Completed or RunStatus.BudgetExhausted or RunStatus.Failed;
}

public sealed record Handoff(string From, string To, string Reason, string HandoverSummary, DateTimeOffset At);

/// <summary>Part progress document (container <c>parts</c>, pk <c>/runId</c>, id <c>part-{day}-{part}</c>).</summary>
public sealed record PartProgress
{
    [JsonPropertyName("id")] public string Id => DocId(Day, Part);
    public required string RunId { get; init; }
    public string Kind { get; init; } = "part";
    public required int Day { get; init; }
    public required int Part { get; init; }
    public string? Title { get; init; }
    public PartState State { get; init; } = PartState.NOT_STARTED;
    public bool PreviouslyCompleted { get; init; }
    public bool TestsPassed { get; init; }
    public string? CandidateAnswer { get; init; }
    public string? AcceptedAnswer { get; init; }
    public int AttemptsUsed { get; init; }
    public int SubmissionsUsed { get; init; }
    public string? CurrentModel { get; init; }
    public string? ActiveAgent { get; init; }
    public bool Escalated { get; init; }
    public string? StatusReason { get; init; }
    public JsonElement? Understanding { get; init; }
    public string? WorkflowRunId { get; init; }
    public List<Handoff> Handoffs { get; init; } = [];
    public DateTimeOffset UpdatedAt { get; init; }

    public static string DocId(int day, int part) => $"part-{day:D2}-{part}";
}

public sealed record Attempt
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    public required string RunId { get; init; }
    public string Kind { get; init; } = "attempt";
    public required int Day { get; init; }
    public required int Part { get; init; }
    public required int Number { get; init; }
    public required string AttemptKind { get; init; } // initial | repair | escalation | post_rejection
    public required string Agent { get; init; }
    public required string ModelKey { get; init; }
    public string? Summary { get; init; }
    public string? Algorithm { get; init; }
    public string? Diagnosis { get; init; }
    public string Outcome { get; init; } = "pending";
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
}

public sealed record CodeRevision
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    public required string RunId { get; init; }
    public string Kind { get; init; } = "revision";
    public required int Day { get; init; }
    public required int Part { get; init; }
    public required int Revision { get; init; }
    public required string Agent { get; init; }
    public required string ModelKey { get; init; }
    public required string Code { get; init; }
    public required string Sha256 { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

public sealed record ModelInvocation
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    public required string RunId { get; init; }
    public string Kind { get; init; } = "model-invocation";
    public int? Day { get; init; }
    public int? Part { get; init; }
    public required string Agent { get; init; }
    public required string ModelKey { get; init; }
    public required string Deployment { get; init; }
    public required string Purpose { get; init; }
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
    public long? ReasoningTokens { get; init; }
    public long LatencyMs { get; init; }
    public double CostUsd { get; init; }
    public string Status { get; init; } = "ok";
    public string? Error { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>
/// Submission (container <c>ledger</c>, pk <c>/year</c>). Account-wide rather than per run, because Advent of Code
/// judges answers per account: a run must see what earlier runs already submitted.
/// </summary>
public sealed record Submission
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    public required int Year { get; init; }
    public string Kind { get; init; } = "submission";
    public string? RunId { get; init; }
    public string? WorkflowRunId { get; init; }
    public required int Day { get; init; }
    public required int Part { get; init; }
    public required string Answer { get; init; }
    public SubmissionOutcome Outcome { get; init; } = SubmissionOutcome.Pending;
    public string? Message { get; init; }
    public int? WaitSeconds { get; init; }
    public DateTimeOffset SubmittedAt { get; init; }
    public DateTimeOffset? RespondedAt { get; init; }
}

/// <summary>Account-wide state per year (container <c>ledger</c>, id <c>account</c>).</summary>
public sealed record AccountState
{
    [JsonPropertyName("id")] public string Id { get; init; } = "account";
    public required int Year { get; init; }
    public string Kind { get; init; } = "account";
    public DateTimeOffset? CooldownUntil { get; init; }
    public Dictionary<string, int> Stars { get; init; } = [];
}

/// <summary>Persisted, ordered domain event (container <c>events</c>, pk <c>/runId</c>, id = zero-padded seq).</summary>
public sealed record DomainEvent
{
    [JsonPropertyName("id")] public string Id => SeqId(Seq);
    public required string RunId { get; init; }
    public required long Seq { get; init; }
    public required DateTimeOffset Ts { get; init; }
    public required string Type { get; init; }
    public string Level { get; init; } = "info";
    public int? Day { get; init; }
    public int? Part { get; init; }
    public string? Agent { get; init; }
    public string Message { get; init; } = "";
    public JsonElement? Data { get; init; }

    /// <summary>Optional RFC 6902 patch against the dashboard state; persisted so replays rebuild the same state.</summary>
    public JsonElement? Patch { get; init; }

    public bool Replay { get; init; }

    public static string SeqId(long seq) => seq.ToString("D10", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>An event before it has been sequenced by the leaseholder.</summary>
public sealed record NewEvent(
    string Type, string Message, string Level = "info", int? Day = null, int? Part = null, string? Agent = null,
    object? Data = null, JsonElement? StatePatch = null);
