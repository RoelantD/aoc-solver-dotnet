using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AGUI.Server;
using Aps.Agent.Abstractions;
using Aps.Agent.Domain;
using Aps.Agent.Persistence;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Aps.Agent.Protocol;

/// <summary>A command could not be executed; reported to the client as a CUSTOM <c>command.error</c> event.</summary>
public sealed class CommandException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// The AG-UI face of the solver. Commands arrive in <c>forwardedProps.command</c>; no
/// natural language reaches a model from the dashboard. Run streams replay persisted events after
/// <c>forwardedProps.afterSeq</c>, then follow live events with a heartbeat; a disconnect only ends the stream.
/// </summary>
public sealed class RunStreamAgent(IApsStore store, EventLog events, RunManager runs, IClock clock, string operatorName,
    Preflight.PreflightService? preflight = null) : AIAgent
{
    public static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(15);

    /// <summary>Stable name required by the AG-UI hosting layer when an agent is selected per request.</summary>
    public override string? Name => "aps-solver";

    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(IEnumerable<ChatMessage> messages,
        AgentSession? session = null, AgentRunOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var input = options is ChatClientAgentRunOptions { ChatOptions: { } co } && co.TryGetRunAgentInput(out var i) ? i : null;
        var props = input?.ForwardedProperties is { ValueKind: JsonValueKind.Object } p ? p : default;
        var command = Str(props, "command") ?? "list";
        var threadId = input?.ThreadId ?? "";
        var args = props.ValueKind == JsonValueKind.Object && props.TryGetProperty("args", out var a) ? a : default;
        var afterSeq = props.ValueKind == JsonValueKind.Object && props.TryGetProperty("afterSeq", out var s) && s.TryGetInt64(out var n) ? n : 0;

        IAsyncEnumerable<AIContent> stream;
        CommandException? failure = null;
        try
        {
            stream = await DispatchAsync(command, threadId, args, afterSeq, cancellationToken);
        }
        catch (CommandException e)
        {
            failure = e;
            stream = Empty();
        }
        catch (KeyNotFoundException e)
        {
            failure = new CommandException("not_found", e.Message);
            stream = Empty();
        }
        if (failure is not null)
        {
            yield return Update(new DomainEventContent(new DomainEvent
            {
                RunId = threadId, Seq = 0, Ts = clock.UtcNow, Type = "command.error", Level = "error",
                Message = failure.Message, Data = Snapshots.Serialize(new { code = failure.Code, command }),
            }));
            yield break;
        }
        await foreach (var content in stream.WithCancellation(cancellationToken)) yield return Update(content);
    }

    private async Task<IAsyncEnumerable<AIContent>> DispatchAsync(string command, string runId, JsonElement args, long afterSeq, CancellationToken ct)
    {
        switch (command)
        {
            case "list":
                return One(new StateSnapshotContent(await Snapshots.ListAsync(store, ct)));
            case "start":
                var created = await CreateRunAsync(args, ct);
                if (!await runs.TryStartAsync(created.Id, ct)) throw new CommandException("lease_lost", "could not acquire the run");
                return AttachAsync(created.Id, 0, ct);
            case "attach":
                return AttachAsync(await RequireRunAsync(runId, ct) is var r ? r.Id : runId, afterSeq, ct);
            case "resume":
            {
                var run = await RequireRunAsync(runId, ct);
                if (run.Status is not (RunStatus.Paused or RunStatus.Created))
                    throw new CommandException("invalid_command", $"run is {run.Status}, not paused");
                await RunDocument.UpdateAsync(store.Runs, runId, x => x with { ControlRequest = null }, ct);
                await runs.TryStartAsync(runId, ct);
                return AttachAsync(runId, afterSeq, ct);
            }
            case "pause" or "stop":
            {
                var run = await RequireRunAsync(runId, ct);
                if (run.IsTerminal) throw new CommandException("invalid_command", $"run is already {run.Status}");
                if (run.Lease is null && command == "stop")
                    await RunDocument.UpdateAsync(store.Runs, runId, x => x with { Status = RunStatus.Stopped, StopReason = "stopped by operator", FinishedAt = clock.UtcNow }, ct);
                else
                    await runs.RequestControlAsync(runId, command, ct);
                return One(new StateSnapshotContent(await Snapshots.RunAsync(store, (await store.Runs.GetAsync(runId, ct))!, ct)));
            }
            case "approve" or "reject":
            {
                var requestId = Str(args, "requestId") ?? throw new CommandException("invalid_command", "requestId is required");
                var run = await RunDocument.UpdateAsync(store.Runs, runId, x =>
                {
                    var target = x.PendingApprovals.FirstOrDefault(p => p.RequestId == requestId)
                                 ?? throw new CommandException("not_found", $"no pending approval {requestId}");
                    if (target.State != ApprovalState.Pending) return x; // decided already: applied exactly once
                    return x with
                    {
                        PendingApprovals = [.. x.PendingApprovals.Select(p => p.RequestId == requestId
                            ? p with { State = command == "approve" ? ApprovalState.Approved : ApprovalState.Rejected, DecidedAt = clock.UtcNow, DecidedBy = operatorName }
                            : p)],
                    };
                }, ct);
                return One(new StateSnapshotContent(await Snapshots.RunAsync(store, run, ct)));
            }
            case "retry":
            {
                var run = await RequireRunAsync(runId, ct);
                if (run.Status == RunStatus.Running) throw new CommandException("run_active", "pause the run before retrying a part");
                var day = Int(args, "day");
                var part = Int(args, "part");
                var pp = await store.Parts.GetPartAsync(runId, day, part, ct) ?? throw new CommandException("not_found", "part not found");
                if (pp.State is not (PartState.FAILED or PartState.BLOCKED or PartState.SKIPPED))
                    throw new CommandException("invalid_command", $"part is {pp.State}");
                await store.Parts.UpsertPartAsync(pp with { State = PartState.NOT_STARTED, AttemptsUsed = 0, SubmissionsUsed = 0, Escalated = false, StatusReason = null }, ct);
                if (run.IsTerminal) await RunDocument.UpdateAsync(store.Runs, runId, x => x with { Status = RunStatus.Paused, FinishedAt = null }, ct);
                return One(new StateSnapshotContent(await Snapshots.RunAsync(store, (await store.Runs.GetAsync(runId, ct))!, ct)));
            }
            case "detail":
            {
                var run = await RequireRunAsync(runId, ct);
                return One(new StateSnapshotContent(await Snapshots.DetailAsync(store, run, Int(args, "day"), Int(args, "part"), ct)));
            }
            case "preflight":
                if (preflight is null) throw new CommandException("internal", "preflight is not available");
                return PreflightAsync(PreflightRequestFrom(args), ct);
            default:
                throw new CommandException("invalid_command", $"unknown command '{command}'");
        }
    }

    /// <summary>Snapshot, then every event after <paramref name="afterSeq"/>, then live events until the run ends.</summary>
    private async IAsyncEnumerable<AIContent> AttachAsync(string runId, long afterSeq, [EnumeratorCancellation] CancellationToken ct)
    {
        var run = await RequireRunAsync(runId, ct);
        // Take over a run whose owner stopped renewing its lease (recycled session, crash, redeploy).
        if (!run.IsTerminal && run.Status != RunStatus.Paused && run.Lease is { } lease && lease.ExpiresAt < clock.UtcNow)
            await runs.TryStartAsync(runId, ct);
        else if (run is { Status: RunStatus.Running or RunStatus.Created, Lease: null }) await runs.TryStartAsync(runId, ct);

        yield return new StateSnapshotContent(await Snapshots.RunAsync(store, run, ct));
        await foreach (var evt in events.TailAsync(runId, afterSeq, Heartbeat, ct, () => IsFinishedAsync(runId, ct)))
            yield return new DomainEventContent(evt);
    }

    /// <summary>One CUSTOM <c>preflight.check</c> per check as it completes, then a snapshot with the summary.</summary>
    private async IAsyncEnumerable<AIContent> PreflightAsync(Preflight.PreflightRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        var results = new List<Preflight.PreflightCheck>();
        await foreach (var check in preflight!.RunAsync(request, ct))
        {
            results.Add(check);
            yield return new DomainEventContent(new DomainEvent
            {
                RunId = "preflight", Seq = 0, Ts = clock.UtcNow, Type = "preflight.check",
                Level = check.Ok ? "success" : check.Skipped ? "warning" : "error",
                Message = $"{check.Check}: {check.Detail}",
                Data = Snapshots.Serialize(new { check = check.Check, ok = check.Ok, detail = check.Detail, ms = check.Ms, missingPermission = check.MissingPermission, skipped = check.Skipped }),
            });
        }
        var failed = results.Where(r => !r.Ok && !r.Skipped).Select(r => r.Check).ToList();
        yield return new StateSnapshotContent(Snapshots.Serialize(new
        {
            preflight = new
            {
                ok = failed.Count == 0, year = request.Year, dayFrom = request.DayFrom, dayTo = request.DayTo,
                passed = results.Count(r => r.Ok), failed, skipped = results.Where(r => r.Skipped).Select(r => r.Check), checks = results,
            },
        }));
    }

    /// <summary><c>{year?, days?: "1-5" | 3, dayFrom?, dayTo?}</c>; the year defaults to the latest finished event.</summary>
    internal Preflight.PreflightRequest PreflightRequestFrom(JsonElement args)
    {
        var year = Int(args, "year", 0);
        if (year == 0)
        {
            year = clock.UtcNow.Year;
            while (EventInProgress(year, clock.UtcNow)) year--;
        }
        int from = Int(args, "dayFrom", 1), to = Int(args, "dayTo", 25);
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("days", out var days))
        {
            if (days.ValueKind == JsonValueKind.Number && days.TryGetInt32(out var single)) from = to = single;
            else if (days.ValueKind == JsonValueKind.String && days.GetString()!.Split('-') is { } parts
                     && int.TryParse(parts[0], out var a) && int.TryParse(parts[^1], out var b))
                (from, to) = (a, b);
        }
        from = Math.Clamp(from, 1, 25);
        return new Preflight.PreflightRequest(year, from, Math.Clamp(to, from, 25));
    }

    private async Task<bool> IsFinishedAsync(string runId, CancellationToken ct)
    {
        var run = await store.Runs.GetAsync(runId, ct);
        return run is null || ((run.IsTerminal || run.Status == RunStatus.Paused) && run.Lease is null);
    }

    private async Task<Run> CreateRunAsync(JsonElement args, CancellationToken ct)
    {
        var year = Int(args, "year", 2024);
        var dayFrom = Math.Clamp(Int(args, "dayFrom", 1), 1, 25);
        var dayTo = Math.Clamp(Int(args, "dayTo", dayFrom), dayFrom, 25);
        if (EventInProgress(year, clock.UtcNow))
            throw new CommandException("year_active", $"Advent of Code {year} is not a past event; live runs target historical years only");
        var active = (await store.Runs.ListAsync(ct)).FirstOrDefault(r => r.Mode == RunMode.Live && !r.IsTerminal && r.Status != RunStatus.Paused);
        if (active is not null) throw new CommandException("run_active", $"run {active.Id} is still active");

        var authorize = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("authorizeSubmissions", out var auth) && auth.ValueKind == JsonValueKind.True;
        var manual = Str(args, "approvalMode") == "manual" || !authorize;
        var limits = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("limits", out var l) && l.ValueKind == JsonValueKind.Object
            ? l.Deserialize<RunConfig>(AguiMapping.Json) ?? new RunConfig() : new RunConfig();
        var config = (limits with { ApprovalMode = manual ? ApprovalMode.Manual : ApprovalMode.Auto }).Validated();
        return await store.Runs.CreateAsync(new Run
        {
            Id = $"run-{clock.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("n")[..4]}",
            Year = year, DayFrom = dayFrom, DayTo = dayTo, AutoSubmit = authorize, AuthorizedAt = authorize ? clock.UtcNow : null,
            Config = config, CreatedAt = clock.UtcNow,
        }, ct);
    }

    /// <summary>
    /// True while a year's event has not finished (a future year, or the current year through December 26, US Eastern). Advent of Code
    /// asks that AI is not used to compete on the leaderboard, so live runs only target past events.
    /// </summary>
    public static bool EventInProgress(int year, DateTimeOffset now)
    {
        var eastern = now.ToOffset(TimeSpan.FromHours(-5));
        return year > eastern.Year || (year == eastern.Year && (eastern.Month < 12 || eastern.Day <= 26));
    }

    private async Task<Run> RequireRunAsync(string runId, CancellationToken ct) =>
        await store.Runs.GetAsync(runId, ct) ?? throw new CommandException("not_found", $"run '{runId}' not found");

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int Int(JsonElement e, string name, int fallback = 0) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : fallback;

    private static AgentResponseUpdate Update(AIContent content) => new(ChatRole.Assistant, [content]);

    private static async IAsyncEnumerable<AIContent> One(AIContent content)
    {
        await Task.CompletedTask;
        yield return content;
    }

    private static async IAsyncEnumerable<AIContent> Empty()
    {
        await Task.CompletedTask;
        yield break;
    }

    protected override Task<AgentResponse> RunCoreAsync(IEnumerable<ChatMessage> messages, AgentSession? session = null,
        AgentRunOptions? options = null, CancellationToken cancellationToken = default) =>
        RunCoreStreamingAsync(messages, session, options, cancellationToken).ToAgentResponseAsync(cancellationToken);

    protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<AgentSession>(new StatelessSession());

    protected override ValueTask<JsonElement> SerializeSessionCoreAsync(AgentSession session, JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default) => ValueTask.FromResult(JsonSerializer.SerializeToElement(new { }));

    protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(JsonElement serializedState, JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default) => ValueTask.FromResult<AgentSession>(new StatelessSession());

    /// <summary>Display name of the caller from the bearer token (not validated here: Foundry authenticates the request).</summary>
    public static string OperatorFrom(HttpContext http)
    {
        var header = http.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return "local operator";
        try
        {
            var payload = header["Bearer ".Length..].Split('.')[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
            foreach (var claim in new[] { "name", "preferred_username", "upn" })
                if (doc.RootElement.TryGetProperty(claim, out var v) && v.GetString() is { Length: > 0 } s) return s;
        }
        catch (Exception e) when (e is FormatException or JsonException or IndexOutOfRangeException) { }
        return "operator";
    }
}

/// <summary>The dashboard protocol keeps no conversation state; run state lives in the store.</summary>
public sealed class StatelessSession : AgentSession;
