using System.Reflection;
using System.Text.Json;
using Aps.Agent.Agents;
using Aps.Agent.Aoc;
using Aps.Agent.Domain;
using Aps.Agent.Execution;
using Aps.Agent.Persistence;
using Microsoft.Agents.AI.Workflows;
using Run = Aps.Agent.Domain.Run;
using RunStatus = Aps.Agent.Domain.RunStatus;

namespace Aps.Agent.Workflow;

/// <summary>
/// The outer run loop: days and parts in order, Part 2 unlock, Day 25, approvals, and run lifecycle. Each part runs
/// as a <see cref="PartWorkflow"/>. Only the leaseholder executes a run: a hosted agent can be recycled or scaled out,
/// and two instances solving the same run would double-submit. Losing the lease stops this instance immediately;
/// all state is persisted, so the new owner resumes safely.
/// </summary>
public sealed class RunOrchestrator(SolverServices svc, string runId, ILogger? logger = null)
{
    public const int FinalDay = 25;
    private static readonly TimeSpan ApprovalPoll = TimeSpan.FromSeconds(1);

    private Run _run = null!;

    public async Task<RunStatus> RunAsync(CancellationToken outer)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        var ct = cts.Token;
        _run = await svc.Store.Runs.GetAsync(runId, ct) ?? throw new KeyNotFoundException(runId);
        await svc.Events.OpenForWriteAsync(runId, ct);
        var renew = RenewLoopAsync(cts);

        var resumed = _run.Status is RunStatus.Paused || _run.StartedAt is not null;
        _run = await svc.Lease.UpdateRunAsync(runId, r => r with { Status = RunStatus.Running, StopReason = null, ControlRequest = null, StartedAt = r.StartedAt ?? svc.Clock.UtcNow }, ct);
        // Advent of Code etiquette: throttle requests to the site; pages and inputs are cached so each is fetched once.
        svc.Aoc.SetMinInterval(TimeSpan.FromSeconds(_run.Config.AocMinIntervalS));
        svc.Aoc.OnFetch = async (what, cached) =>
            await Emit(new NewEvent("aoc.fetch", $"{(cached ? "Loaded cached" : "Fetched")} {what}", Day: _run.CurrentDay, Part: _run.CurrentPart,
                Agent: AgentIds.System, Data: new { what, cached }), CancellationToken.None);
        if (svc.Lease is { TookOver: true })
            await Emit(new NewEvent("run.lease", "Run taken over by this instance after the previous owner stopped renewing", Agent: AgentIds.System,
                Data: new { @event = "taken_over" }), ct);
        await Emit(new NewEvent(resumed ? "run.resumed" : "run.started",
            $"Run {(resumed ? "resumed" : "started")}: {_run.Year} days {_run.DayFrom}–{_run.DayTo} " +
            $"({(_run.Config.ApprovalMode == ApprovalMode.Manual ? "manual approval mode" : "automatic submission authorized")})",
            Data: new { status = "running" }, StatePatch: PartSession.Patch(("replace", "/run/status", "running"))), ct);

        var (status, reason) = (RunStatus.Completed, (string?)null);
        try
        {
            await PrepareAsync(ct);
            for (var day = _run.DayFrom; day <= _run.DayTo; day++)
                for (var part = 1; part <= 2; part++)
                {
                    svc.Control.ThrowIfPauseRequested();
                    var (d, p) = (day, part);
                    _run = await svc.Lease.UpdateRunAsync(runId, r => r with { CurrentDay = d, CurrentPart = p }, ct);
                    await ProcessPartAsync(day, part, ct);
                }
        }
        catch (Exception e) when (Find<LeaseLostException>(e) is not null || (ct.IsCancellationRequested && !outer.IsCancellationRequested))
        {
            logger?.LogWarning("Lease for run {RunId} lost; stopping without finalizing", runId);
            svc.Events.CloseForWrite(runId);
            return RunStatus.Running; // another owner continues the run
        }
        catch (Exception e) when (Find<PauseRunException>(e) is { } pause)
        {
            (status, reason) = (pause.Stop ? RunStatus.Stopped : RunStatus.Paused, pause.Message);
        }
        catch (Exception e) when (Find<BudgetExhaustedException>(e) is { } budget)
        {
            (status, reason) = (RunStatus.BudgetExhausted, budget.Message);
        }
        catch (Exception e) when (Find<AocAuthException>(e) is { } auth)
        {
            (status, reason) = (RunStatus.Paused, $"Advent of Code session problem: {auth.Message}");
        }
        catch (Exception e) when (Find<AocException>(e) is { } aoc)
        {
            (status, reason) = (RunStatus.Paused, $"Advent of Code unavailable: {aoc.Message}");
        }
        catch (OperationCanceledException) when (outer.IsCancellationRequested)
        {
            (status, reason) = (RunStatus.Paused, "agent host shutting down");
        }
        catch (Exception e)
        {
            var inner = Unwrap(e);
            logger?.LogError(inner, "Run {RunId} crashed", runId);
            await Emit(new NewEvent("error", $"Internal error: {inner.GetType().Name}: {Cap(inner.Message)}", "error", Agent: AgentIds.System,
                Data: new { kind = "internal", detail = Cap(inner.Message, 500) }), CancellationToken.None);
            (status, reason) = (RunStatus.Failed, $"internal error: {inner.GetType().Name}");
        }
        finally
        {
            await cts.CancelAsync();
            try { await renew; } catch (OperationCanceledException) { }
        }

        await FinalizeAsync(status, reason);
        return status;
    }

    private async Task FinalizeAsync(RunStatus status, string? reason)
    {
        var ct = CancellationToken.None;
        var terminal = status is RunStatus.Completed or RunStatus.Stopped or RunStatus.BudgetExhausted or RunStatus.Failed;
        try
        {
            _run = await svc.Lease.UpdateRunAsync(runId, r => r with
            {
                Status = status, StopReason = reason, ActiveAgent = null, ControlRequest = null, FinishedAt = terminal ? svc.Clock.UtcNow : r.FinishedAt,
            }, ct);
        }
        catch (LeaseLostException)
        {
            return;
        }
        var wire = JsonSerializer.SerializeToElement(status).GetString();
        var (level, message) = status switch
        {
            RunStatus.Completed => ("success", "Run completed: selected day range finished"),
            RunStatus.Paused => ("warning", $"Run paused: {reason}"),
            RunStatus.Stopped => ("warning", $"Run stopped: {reason}"),
            RunStatus.BudgetExhausted => ("error", $"Run stopped: {reason}"),
            _ => ("error", $"Run failed: {reason}"),
        };
        await Emit(new NewEvent($"run.{wire}", message, level, Data: new { status = wire, reason },
            StatePatch: PartSession.Patch(("replace", "/run/status", wire), ("replace", "/activeAgent", null))), ct);
        await svc.Lease.ReleaseAsync(runId, ct);
        svc.Events.CloseForWrite(runId);
    }

    /// <summary>
    /// Keeps the lease alive while the run executes. If renewal finds another owner (this instance stalled past the
    /// lease TTL and was taken over), the run is cancelled here so two instances never act on the same run.
    /// </summary>
    private async Task RenewLoopAsync(CancellationTokenSource cts)
    {
        try
        {
            while (!cts.IsCancellationRequested)
            {
                await Task.Delay(RunLease.RenewInterval, cts.Token);
                await svc.Lease.RenewAsync(runId, cts.Token);
            }
        }
        catch (LeaseLostException)
        {
            await cts.CancelAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            logger?.LogWarning(e, "Lease renew failed for run {RunId}; will retry", runId);
        }
    }

    private Task Emit(NewEvent e, CancellationToken ct) => svc.Events.AppendAsync(runId, e, ct);

    // ------------------------------------------------------------------ preparation

    private async Task PrepareAsync(CancellationToken ct)
    {
        if (!await svc.Aoc.HasSessionAsync(ct)) throw new PauseRunException("the Advent of Code session is not configured");
        var stars = await svc.Aoc.CalendarAsync(_run.Year, refresh: true, ct);
        var account = await svc.Store.Ledger.GetAccountAsync(_run.Year, ct);
        await svc.Store.Ledger.UpsertAccountAsync(account with
        {
            Stars = stars.ToDictionary(kv => kv.Key.ToString(System.Globalization.CultureInfo.InvariantCulture), kv => kv.Value),
        }, ct);
        for (var day = _run.DayFrom; day <= _run.DayTo; day++)
            for (var part = 1; part <= 2; part++)
            {
                var pp = await svc.Store.Parts.GetPartAsync(runId, day, part, ct)
                         ?? new PartProgress { RunId = runId, Day = day, Part = part, UpdatedAt = svc.Clock.UtcNow };
                var own = (await svc.Store.Ledger.ListSubmissionsAsync(_run.Year, day, part, ct)).Any(s => s.RunId == runId);
                if (pp.State != PartState.VERIFIED && !own && stars.GetValueOrDefault(day) >= part)
                {
                    pp = pp with { State = PartState.VERIFIED, PreviouslyCompleted = true };
                    await svc.Store.Parts.UpsertPartAsync(pp, ct);
                    await Emit(new NewEvent("aoc.previously_completed", $"Day {day:D2} Part {part} was already completed on this account", "success", day, part,
                        AgentIds.System, new { answer_known = false }, PartSession.Patch(("replace", $"/calendar/{day - 1}/{part - 1}", "VERIFIED"))), ct);
                }
                else
                {
                    await svc.Store.Parts.UpsertPartAsync(pp, ct);
                }
            }
    }

    // ------------------------------------------------------------------ per part

    private async Task ProcessPartAsync(int day, int part, CancellationToken ct)
    {
        var pp = await svc.Store.Parts.GetPartAsync(runId, day, part, ct) ?? throw new InvalidOperationException("part missing");
        if (PartStates.Terminal.Contains(pp.State)) return;

        if (part == 2 && (await svc.Store.Parts.GetPartAsync(runId, day, 1, ct))?.State != PartState.VERIFIED)
        {
            await SetStateAsync(pp, PartState.BLOCKED, "Part 1 is not verified", ct);
            return;
        }

        string? pendingAnswer = null;
        var open = (await svc.Store.Ledger.ListSubmissionsAsync(_run.Year, day, part, ct))
            .LastOrDefault(s => s.Outcome is SubmissionOutcome.Pending or SubmissionOutcome.Uncertain);
        if (open is not null)
        {
            // An earlier submission may or may not have reached the server. Never repeat it blindly: the submit
            // step first checks the account state.
            await Emit(new NewEvent("submit.result", $"Previous submission of {open.Answer} has an uncertain outcome; reconciling with the account state",
                "warning", day, part, AgentIds.System, new { outcome = "UNCERTAIN" }), ct);
            pendingAnswer = open.Answer;
        }

        await SetStateAsync(pp, PartState.FETCHING, null, ct);
        await Emit(new NewEvent("agent.step", "Loading puzzle…", Day: day, Part: part, Agent: AgentIds.System), ct);
        DayPage page;
        try
        {
            page = await svc.Aoc.DayPageAsync(_run.Year, day, refresh: false, ct);
            if (part == 2 && page.Parts.Count < 2 && !page.CompletedParts.Contains(2))
                page = await svc.Aoc.DayPageAsync(_run.Year, day, refresh: true, ct);
        }
        catch (AocLockedException e)
        {
            await SetStateAsync(pp, PartState.BLOCKED, e.Message, ct);
            return;
        }
        pp = await svc.Store.Parts.GetPartAsync(runId, day, part, ct) ?? pp;
        var title = page.Title ?? $"Day {day}";
        if (pp.Title != title) await svc.Store.Parts.UpsertPartAsync(pp = pp with { Title = title }, ct);

        if (open is null && page.CompletedAnswers.TryGetValue(part, out var known))
        {
            await svc.Store.Parts.UpsertPartAsync(pp = pp with { PreviouslyCompleted = true, AcceptedAnswer = known }, ct);
            await SetStateAsync(pp, PartState.VERIFIED, null, ct);
            await Emit(new NewEvent("aoc.previously_completed", $"Day {day:D2} Part {part} was already completed on this account", "success", day, part,
                AgentIds.System, new { answer_known = known is not null }), ct);
            return;
        }

        if (day == FinalDay && part == 2)
        {
            await FinalDayClaimAsync(pp, page, ct);
            return;
        }
        if (page.Parts.Count < part && open is null)
        {
            await SetStateAsync(pp, PartState.BLOCKED, "Part description not available", ct);
            return;
        }

        var input = await svc.Aoc.InputAsync(_run.Year, day, ct);
        var puzzle = await BuildPuzzleAsync(pp, page, title, input, ct);
        if (part == 2)
            await Emit(new NewEvent("part.unlocked", $"Day {day:D2} Part 2 unlocked: {title}", "success", day, 2, AgentIds.System), ct);

        // Continue the part's workflow from its latest checkpoint. An uncertain submission is settled first, from the
        // ledger, by a fresh workflow that goes straight to the submit step.
        var resumeFrom = pendingAnswer is null ? await LatestCheckpointAsync(pp, ct) : null;

        // Without a checkpoint, a decided-but-unapplied or pending approval is still honoured before solving again.
        // With one, the checkpoint holds the approval request: on resume MAF re-emits the pending RequestInfoEvent
        // with the same RequestId.
        if (resumeFrom is null && pendingAnswer is null &&
            _run.PendingApprovals.FirstOrDefault(a => a.Day == day && a.Part == part && a.AppliedAt is null) is { } approval)
        {
            var decided = await WaitForDecisionAsync(approval.RequestId, ct);
            await MarkAppliedAsync(decided.RequestId, ct);
            if (decided.State == ApprovalState.Approved) pendingAnswer = decided.Answer;
        }

        var outcome = await RunPartAsync(pp, puzzle, new StartPart(pendingAnswer), resumeFrom, ct);
        await HandleOutcomeAsync(day, part, outcome, ct);
    }

    private async Task<PuzzleMaterial> BuildPuzzleAsync(PartProgress pp, DayPage page, string title, string input, CancellationToken ct)
    {
        var relevant = page.Parts.Take(pp.Part).ToList();
        var article = relevant.Count >= pp.Part ? relevant[pp.Part - 1] : relevant.LastOrDefault();
        string? p1Summary = null, p1Answer = null, p1Code = null;
        if (pp.Part == 2 && await svc.Store.Parts.GetPartAsync(runId, pp.Day, 1, ct) is { } p1)
        {
            p1Answer = p1.AcceptedAnswer;
            if (p1.Understanding is { ValueKind: JsonValueKind.Object } u)
                p1Summary = $"{Str(u, "summary")} {Str(u, "algorithm")}".Trim();
            p1Code = (await svc.Store.Parts.ListRevisionsAsync(runId, pp.Day, 1, ct)).LastOrDefault()?.Code;
        }
        return new PuzzleMaterial(title, string.Join("\n\n", relevant.Select(a => a.Text)), [.. relevant.SelectMany(a => a.PreBlocks)],
            article?.Emphasized ?? [], article?.Text ?? "", input, p1Summary, p1Answer, p1Code);
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private CheckpointManager Checkpoints() => CheckpointManager.CreateJson(svc.Store.CheckpointsFor(runId));

    /// <summary>The latest checkpoint of the part's current workflow, or null when it has none.</summary>
    private async Task<CheckpointInfo?> LatestCheckpointAsync(PartProgress pp, CancellationToken ct) =>
        pp.WorkflowRunId is { } session ? await Checkpoints().GetLatestCheckpointAsync(session, ct) : null;

    /// <summary>
    /// Runs the part workflow with checkpointing, or resumes it from <paramref name="resumeFrom"/>, and services its
    /// approval requests. MAF writes a checkpoint after every superstep (one round of executors running on the
    /// messages from the previous round); a new owner of the run (restart, recycled session, redeploy) resumes the part
    /// from the last one instead of starting it over.
    /// </summary>
    private async Task<PartOutcome> RunPartAsync(PartProgress pp, PuzzleMaterial puzzle, StartPart start, CheckpointInfo? resumeFrom, CancellationToken ct)
    {
        var workflowRunId = resumeFrom?.SessionId ?? $"{runId}-d{pp.Day:D2}-p{pp.Part}-{Guid.NewGuid().ToString("n")[..8]}";
        if (pp.WorkflowRunId != workflowRunId) await svc.Store.Parts.UpsertPartAsync(pp = pp with { WorkflowRunId = workflowRunId }, ct);
        var session = new PartSession(svc, _run, pp, puzzle);
        if (resumeFrom is null) await RestoreMemoryAsync(session, ct); // otherwise AnalystExecutor restores it from the checkpoint
        var workflow = PartWorkflow.Build(session);
        var checkpoints = Checkpoints();

        // One trace per part: a root span, so a detached run's part does not hang under an unrelated request.
        var parent = System.Diagnostics.Activity.Current;
        System.Diagnostics.Activity.Current = null;
        using var span = Observability.Telemetry.StartStep($"aps.part d{pp.Day:D2} p{pp.Part}", session.Year, pp.Day, pp.Part);
        span?.SetTag("aps.run_id", runId);
        span?.SetTag("aps.workflow_run_id", workflowRunId);
        span?.SetTag("aps.resumed_from_checkpoint", resumeFrom?.CheckpointId);
        try
        {
            await using var run = resumeFrom is null
                ? await InProcessExecution.RunStreamingAsync(workflow, start, checkpoints, workflowRunId, ct)
                : await InProcessExecution.ResumeStreamingAsync(workflow, resumeFrom, checkpoints, ct);
            if (resumeFrom is not null)
                await Emit(new NewEvent("workflow.resumed", $"Workflow resumed from checkpoint {Short(resumeFrom.CheckpointId)}", "success", pp.Day, pp.Part,
                    AgentIds.System, new { workflowRunId, checkpointId = resumeFrom.CheckpointId }), ct);
            // SuperStepCompletedEvent does not say which executors ran, so collect them from ExecutorCompletedEvent
            // to show what each checkpoint covers.
            var completed = new List<string>();
            await foreach (var evt in run.WatchStreamAsync(ct))
            {
                switch (evt)
                {
                    case ExecutorCompletedEvent done when done.ExecutorId is { } id:
                        completed.Add(id);
                        break;
                    case SuperStepCompletedEvent { CompletionInfo.Checkpoint: { } checkpoint } step:
                        var executors = completed.Distinct().ToList();
                        completed.Clear();
                        span?.AddEvent(new System.Diagnostics.ActivityEvent("checkpoint", tags: new() { ["aps.checkpoint_id"] = checkpoint.CheckpointId, ["aps.step"] = step.StepNumber }));
                        await Emit(new NewEvent("workflow.checkpoint", $"Checkpoint saved after {string.Join(", ", executors)}", Day: pp.Day, Part: pp.Part,
                            Agent: AgentIds.System, Data: new { workflowRunId, checkpointId = checkpoint.CheckpointId, step = step.StepNumber, executors }), ct);
                        break;
                    case WorkflowOutputEvent { Data: PartOutcome outcome }:
                        _run = session.Run;
                        Observability.Telemetry.SetOutcome(span, outcome.Result.ToString().ToLowerInvariant(), outcome.Result != PartResult.Failed);
                        span?.SetTag("aps.reason", outcome.Reason);
                        return outcome;
                    case RequestInfoEvent info when info.Request.TryGetDataAs<CandidateReady>(out var candidate):
                        span?.AddEvent(new System.Diagnostics.ActivityEvent("approval.requested"));
                        var decision = await RequestApprovalAsync(pp.Day, pp.Part, info.Request.RequestId, candidate, ct);
                        await run.SendResponseAsync(info.Request.CreateResponse(decision));
                        break;
                    case WorkflowErrorEvent error:
                        throw Unwrap(error.Data as Exception ?? new InvalidOperationException("workflow failed"));
                    case ExecutorFailedEvent failed:
                        throw Unwrap(failed.Data as Exception ?? new InvalidOperationException($"executor {failed.ExecutorId} failed"));
                }
            }
            if (resumeFrom is null) throw new InvalidOperationException("part workflow ended without an outcome");
        }
        catch (Exception e) when (span is not null && span.Status == System.Diagnostics.ActivityStatusCode.Unset)
        {
            // Pause and stop are operator or infrastructure control, not a failure of the part.
            if (e is PauseRunException or OperationCanceledException)
                Observability.Telemetry.SetOutcome(span, e is PauseRunException ? "paused" : "cancelled", true);
            else
                Observability.Telemetry.SetOutcome(span, e.GetType().Name, false);
            throw;
        }
        finally
        {
            System.Diagnostics.Activity.Current = parent;
        }

        // The checkpoint had no work left (the outcome was yielded, then the owner stopped before recording it):
        // start the part's workflow over; the ledger keeps a submission from repeating.
        await Emit(new NewEvent("workflow.restarted", "The last checkpoint had no remaining work; starting the part's workflow again", "warning",
            pp.Day, pp.Part, AgentIds.System, new { workflowRunId }), ct);
        return await RunPartAsync(pp, puzzle, start, null, ct);
    }

    private static string Short(string id) => id.Length <= 8 ? id : id[..8];

    /// <summary>Rebuilds what earlier attempts in this part learned (resume after restart or recycle).</summary>
    private async Task RestoreMemoryAsync(PartSession s, CancellationToken ct)
    {
        foreach (var a in await svc.Store.Parts.ListAttemptsAsync(runId, s.Day, s.Part, ct))
            if (a.Outcome is not ("pending" or "infra_error"))
                s.History.Add(new AttemptHistoryItem(a.Number, AgentIds.DisplayName(a.Agent), a.Algorithm ?? "", a.Outcome));
        s.PreviousCode = (await svc.Store.Parts.ListRevisionsAsync(runId, s.Day, s.Part, ct)).LastOrDefault()?.Code;
        if (s.Progress.Understanding is { ValueKind: JsonValueKind.Object } u)
            try { s.Analysis = u.Deserialize<Analysis>(); } catch (JsonException) { }
        if (s.Progress.Handoffs.LastOrDefault() is { } h) s.HandoverSummary = h.HandoverSummary;
    }

    // ------------------------------------------------------------------ approvals

    private async Task<ApprovalDecision> RequestApprovalAsync(int day, int part, string requestId, CandidateReady candidate, CancellationToken ct)
    {
        // A workflow resumed from a checkpoint asks again with the same request id: keep that approval, and any decision
        // the operator already made on it.
        var known = (await svc.Store.Runs.GetAsync(runId, ct))?.PendingApprovals.Any(a => a.RequestId == requestId) == true;
        // A resumed part went through FETCHING before the workflow re-asked: show it as waiting again.
        if (await svc.Store.Parts.GetPartAsync(runId, day, part, ct) is { } waiting)
            await SetStateAsync(waiting, PartState.WAITING_FOR_APPROVAL, null, ct);
        if (!known)
        {
            var request = new ApprovalRequest { RequestId = requestId, Day = day, Part = part, Answer = candidate.Answer };
            _run = await svc.Lease.UpdateRunAsync(runId, r => r with { PendingApprovals = [.. r.PendingApprovals.Where(a => a.RequestId != requestId), request] }, ct);
            await Emit(new NewEvent("approval.requested", $"Candidate answer {candidate.Answer} awaits operator approval", Day: day, Part: part,
                Agent: AgentIds.System, Data: new { requestId, answer = candidate.Answer },
                StatePatch: PartSession.Patch(("replace", "/pendingApprovals", _run.PendingApprovals))), ct);
        }
        var decided = await WaitForDecisionAsync(requestId, ct);
        await Emit(new NewEvent("approval.decided", $"Operator {(decided.State == ApprovalState.Approved ? "approved" : "rejected")} {candidate.Answer}",
            decided.State == ApprovalState.Approved ? "success" : "warning", day, part, AgentIds.System,
            new { requestId, decision = decided.State == ApprovalState.Approved ? "approved" : "rejected", decidedBy = decided.DecidedBy }), ct);
        await MarkAppliedAsync(requestId, ct);
        return new ApprovalDecision(candidate, decided.State == ApprovalState.Approved, decided.DecidedBy);
    }

    private async Task<ApprovalRequest> WaitForDecisionAsync(string requestId, CancellationToken ct)
    {
        while (true)
        {
            svc.Control.ThrowIfPauseRequested();
            var run = await svc.Store.Runs.GetAsync(runId, ct);
            var approval = run?.PendingApprovals.FirstOrDefault(a => a.RequestId == requestId)
                           ?? throw new InvalidOperationException($"approval {requestId} disappeared");
            if (approval.State != ApprovalState.Pending) return approval;
            await Task.Delay(ApprovalPoll, ct);
        }
    }

    private async Task MarkAppliedAsync(string requestId, CancellationToken ct)
    {
        _run = await svc.Lease.UpdateRunAsync(runId, r => r with
        {
            PendingApprovals = [.. r.PendingApprovals.Where(a => a.RequestId != requestId)],
        }, ct);
        await Emit(new NewEvent("agent.step", "Operator decision applied", Agent: AgentIds.System,
            StatePatch: PartSession.Patch(("replace", "/pendingApprovals", _run.PendingApprovals))), ct);
    }

    // ------------------------------------------------------------------ outcomes

    private async Task HandleOutcomeAsync(int day, int part, PartOutcome outcome, CancellationToken ct)
    {
        var pp = await svc.Store.Parts.GetPartAsync(runId, day, part, ct)!;
        switch (outcome.Result)
        {
            case PartResult.Verified when part == 1 && day != FinalDay:
                await Emit(new NewEvent("agent.step", "Unlocking Part 2", Day: day, Part: 2, Agent: AgentIds.System), ct);
                await svc.Aoc.DayPageAsync(_run.Year, day, refresh: true, ct); // Part 2 text becomes available only now
                break;
            case PartResult.Failed:
                await SetStateAsync(pp!, PartState.FAILED, outcome.Reason, ct);
                await Emit(new NewEvent("part.failed", $"Day {day:D2} Part {part} failed: {outcome.Reason}", "error", day, part, AgentIds.System,
                    new { reason = outcome.Reason }), ct);
                if (part == 1 && await svc.Store.Parts.GetPartAsync(runId, day, 2, ct) is { } p2 && !PartStates.Terminal.Contains(p2.State))
                    await SetStateAsync(p2, PartState.BLOCKED, "Part 1 failed", ct);
                break;
            case PartResult.Blocked:
                await SetStateAsync(pp!, PartState.BLOCKED, outcome.Reason, ct);
                break;
        }
    }

    /// <summary>
    /// Day 25 Part 2 has no puzzle: the final star is granted once all 49 other stars are earned, by submitting the
    /// page's form. There is nothing to solve, so it goes straight to the submit step.
    /// </summary>
    private async Task FinalDayClaimAsync(PartProgress pp, DayPage page, CancellationToken ct)
    {
        var stars = await svc.Aoc.CalendarAsync(_run.Year, refresh: true, ct);
        var others = Enumerable.Range(1, FinalDay - 1).Sum(d => Math.Min(stars.GetValueOrDefault(d), 2));
        var p1 = stars.GetValueOrDefault(FinalDay) >= 1;
        if (others < 2 * (FinalDay - 1) || !p1)
        {
            await SetStateAsync(pp, PartState.SKIPPED, $"needs all 49 other stars (account has {others + (p1 ? 1 : 0)})", ct);
            await Emit(new NewEvent("part.skipped", "Day 25 Part 2 skipped: the final star requires all 49 other stars", "warning", FinalDay, 2, AgentIds.System), ct);
            return;
        }
        await Emit(new NewEvent("agent.step", "All 49 other stars present — claiming the final star", Day: FinalDay, Part: 2, Agent: AgentIds.System), ct);
        var puzzle = new PuzzleMaterial(page.Title ?? "Day 25", "", [], [], "", "");
        var outcome = await RunPartAsync(pp, puzzle, new StartPart(page.FormHiddenAnswer ?? "0"), null, ct);
        await HandleOutcomeAsync(FinalDay, 2, outcome, ct);
    }

    private async Task SetStateAsync(PartProgress pp, PartState state, string? reason, CancellationToken ct)
    {
        if (pp.State == state) return;
        await svc.Store.Parts.UpsertPartAsync(pp with { State = state, StatusReason = reason ?? pp.StatusReason, UpdatedAt = svc.Clock.UtcNow }, ct);
        await Emit(new NewEvent("part.state", $"{pp.State} → {state}", Day: pp.Day, Part: pp.Part, Agent: AgentIds.System,
            Data: new { from = pp.State.ToString(), to = state.ToString(), reason },
            StatePatch: PartSession.Patch(("replace", $"/calendar/{pp.Day - 1}/{pp.Part - 1}", state.ToString()))), ct);
    }

    // ------------------------------------------------------------------ helpers

    private static T? Find<T>(Exception e) where T : Exception
    {
        for (Exception? x = e; x is not null; x = x.InnerException)
        {
            if (x is T t) return t;
            if (x is AggregateException agg)
                foreach (var inner in agg.InnerExceptions)
                    if (Find<T>(inner) is { } found) return found;
        }
        return null;
    }

    private static Exception Unwrap(Exception e)
    {
        while (e is TargetInvocationException or AggregateException { InnerExceptions.Count: 1 } && e.InnerException is { } inner) e = inner;
        return e;
    }

    private static string Cap(string s, int n = 300) => s.Length <= n ? s : s[..n];
}
