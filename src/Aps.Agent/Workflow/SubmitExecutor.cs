using Aps.Agent.Aoc;
using Aps.Agent.Domain;
using Aps.Agent.Verification;
using Microsoft.Agents.AI.Workflows;

namespace Aps.Agent.Workflow;

/// <summary>
/// System step: submit to Advent of Code. Every POST passes the persisted <see cref="SubmissionGate"/> first and is
/// preceded by a PENDING record, so a crash between POST and response can never hide that an answer was sent.
/// Handles both an auto-mode candidate and a manual-mode approval decision from the request port.
/// </summary>
public sealed class SubmitExecutor(PartSession s) : Executor("submit")
{
    private const int ReconcileWaitSeconds = 60;
    private PartSession S { get; } = s;

    protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder protocolBuilder) =>
        protocolBuilder
            .ConfigureRoutes(r => r
                .AddHandler<CandidateReady>((c, ctx, ct) => SubmitAsync(c, ctx, ct))
                .AddHandler<ApprovalDecision>((d, ctx, ct) => DecideAsync(d, ctx, ct)))
            .SendsMessage<CodeRequest>()
            .YieldsOutput<PartOutcome>();

    private async ValueTask DecideAsync(ApprovalDecision decision, IWorkflowContext context, CancellationToken ct)
    {
        if (decision.Approved)
        {
            await SubmitAsync(decision.Candidate, context, ct);
            return;
        }
        // A candidate the operator rejected counts as a failed attempt; it is never submitted.
        S.LastFailure = "wrong";
        S.AfterRejection = true;
        S.Feedback.Add($"The operator rejected the candidate answer {decision.Candidate.Answer}. Re-read the puzzle and look for a misread requirement.");
        await context.SendMessageAsync(new CodeRequest(), ct);
    }

    private async ValueTask SubmitAsync(CandidateReady candidate, IWorkflowContext context, CancellationToken ct)
    {
        await S.SetActiveAgentAsync(AgentIds.System, ct);
        var answer = candidate.Answer;
        while (true)
        {
            S.PausePoint();
            var gate = await S.S.Gate.EvaluateAsync(S.Year, S.Day, S.Part, answer, S.Cfg.MaxSubmissionsPerPart, ct);
            switch (gate.Verdict)
            {
                case GateVerdict.MustReconcile:
                    if (await ReconcileAsync(gate.Unresolved, ct) == "verified")
                    {
                        await context.YieldOutputAsync(new PartOutcome(PartResult.Verified, "accepted (reconciled)", S.Progress.AcceptedAnswer), ct);
                        return;
                    }
                    continue;
                case GateVerdict.Cooldown:
                    await S.SetStateAsync(PartState.WAITING_FOR_COOLDOWN, null, ct);
                    await S.EmitAsync("submit.cooldown", $"Waiting for Advent of Code cooldown until {gate.CooldownUntil:HH:mm:ss} UTC", "warning",
                        AgentIds.System, new { until = gate.CooldownUntil }, ct: ct);
                    await S.S.Control.SleepAsync(S.S.Clock, gate.CooldownUntil!.Value - S.S.Clock.UtcNow, ct);
                    continue;
                case GateVerdict.Duplicate:
                    S.LastFailure = "wrong";
                    S.Feedback.Add($"Your program produced {answer}, but {gate.Reason}.");
                    await S.EmitAsync("submit.skipped_duplicate", $"Not submitting: {gate.Reason}", "warning", AgentIds.System, new { reason = gate.Reason }, ct: ct);
                    await context.SendMessageAsync(new CodeRequest(), ct);
                    return;
                case GateVerdict.LimitReached:
                    await context.YieldOutputAsync(new PartOutcome(PartResult.Failed, gate.Reason), ct);
                    return;
            }

            var result = await PostAsync(answer, ct);
            switch (result)
            {
                case "verified":
                    await context.YieldOutputAsync(new PartOutcome(PartResult.Verified, "accepted", answer), ct);
                    return;
                case "rejected":
                    S.AfterRejection = true;
                    await context.SendMessageAsync(new CodeRequest(), ct);
                    return;
                case "blocked":
                    await context.YieldOutputAsync(new PartOutcome(PartResult.Blocked, "Advent of Code reports this level is not available"), ct);
                    return;
                default: // retry_same: cooldown or an answer that never reached the server
                    continue;
            }
        }
    }

    /// <summary>Submits once. Returns verified | rejected | retry_same | blocked.</summary>
    private async Task<string> PostAsync(string answer, CancellationToken ct)
    {
        await S.SetStateAsync(PartState.SUBMITTING, null, ct);
        var pending = await S.S.Gate.RecordPendingAsync(S.Run.Id, S.Progress.WorkflowRunId, S.Year, S.Day, S.Part, answer, ct);
        await S.EmitAsync("submit.sent", $"Submitting candidate answer {answer}", agent: AgentIds.System, data: new { answer }, ct: ct);
        SubmitResult res;
        try
        {
            res = await S.S.Aoc.SubmitAsync(S.Year, S.Day, S.Part, answer, ct);
        }
        catch (AocNotSentException e)
        {
            await S.S.Gate.CompleteAsync(pending, SubmissionOutcome.Unexpected, $"not sent: {e.Message}", null, ct);
            throw new PauseRunException($"could not reach Advent of Code ({e.Message}); nothing was submitted");
        }
        catch (AocUncertainException e)
        {
            var uncertain = await S.S.Gate.CompleteAsync(pending, SubmissionOutcome.Uncertain, $"no response: {e.Message}", null, ct);
            await S.EmitAsync("submit.result", "Submission outcome uncertain (connection lost); reconciling with the account state before any further submission",
                "warning", AgentIds.System, new { outcome = "UNCERTAIN" }, ct: ct);
            return await ReconcileAsync(uncertain, ct);
        }
        catch (AocAuthException)
        {
            await S.S.Gate.CompleteAsync(pending, SubmissionOutcome.AuthFailed, "session invalid", null, ct);
            throw new PauseRunException("Advent of Code session expired or invalid; update the aoc-session secret and resume");
        }

        await S.S.Gate.CompleteAsync(pending, res.Outcome, res.Message, res.WaitSeconds, ct);
        switch (res.Outcome)
        {
            case SubmissionOutcome.Correct:
                await VerifiedAsync(answer, ct);
                return "verified";
            case SubmissionOutcome.Incorrect or SubmissionOutcome.TooHigh or SubmissionOutcome.TooLow:
            {
                await S.SaveAsync(p => p with { SubmissionsUsed = p.SubmissionsUsed + 1 }, ct);
                var hint = res.Outcome switch { SubmissionOutcome.TooHigh => " (too high)", SubmissionOutcome.TooLow => " (too low)", _ => "" };
                await S.EmitAsync("submit.result", $"Advent of Code rejected {answer}{hint}", "error", AgentIds.System,
                    new { outcome = res.Outcome, wait_seconds = res.WaitSeconds, message = Cap(res.Message) }, ct: ct);
                var said = res.Outcome switch { SubmissionOutcome.TooHigh => "it is too high", SubmissionOutcome.TooLow => "it is too low", _ => "it is wrong" };
                S.Feedback.Add($"Advent of Code rejected the answer {answer}: {said}. The examples passed, so the bug is likely in an edge case of the real input, " +
                               "a misread requirement, or an assumption that only holds for the examples. Re-read the puzzle carefully and diagnose before changing the code.");
                S.LastFailure = "wrong";
                return "rejected";
            }
            case SubmissionOutcome.Cooldown:
                await S.EmitAsync("submit.result", $"Advent of Code asked to wait {res.WaitSeconds ?? 60}s before submitting", "warning", AgentIds.System,
                    new { outcome = "COOLDOWN", wait_seconds = res.WaitSeconds ?? 60 }, ct: ct);
                // The gate already stored the wait Advent of Code named; if it named none, wait a minute anyway.
                if (res.WaitSeconds is null) await S.S.Gate.SetCooldownAsync(S.Year, S.S.Clock.UtcNow.AddSeconds(61), ct);
                return "retry_same";
            case SubmissionOutcome.AlreadyCompleted:
                await S.EmitAsync("submit.result", "Advent of Code reports this part is already complete; reconciling", "warning", AgentIds.System,
                    new { outcome = "ALREADY_COMPLETED" }, ct: ct);
                return await ReconcileAsync(null, ct);
            case SubmissionOutcome.Locked:
                await S.SetStateAsync(PartState.BLOCKED, "Advent of Code reports this level is not available", ct);
                await S.EmitAsync("submit.result", "Part is not available on Advent of Code", "error", AgentIds.System, new { outcome = "LOCKED" }, ct: ct);
                return "blocked";
            case SubmissionOutcome.AuthFailed:
                await S.EmitAsync("submit.result", "Advent of Code session is not valid", "error", AgentIds.System, new { outcome = "AUTH_FAILED" }, ct: ct);
                throw new PauseRunException("Advent of Code session expired or invalid; update the aoc-session secret and resume");
            default:
                await S.EmitAsync("submit.result", "Unexpected response from Advent of Code; reconciling", "warning", AgentIds.System,
                    new { outcome = "UNEXPECTED", message = Cap(res.Message) }, ct: ct);
                if (await ReconcileAsync(null, ct) != "verified")
                    throw new PauseRunException("unexpected submission response; inspect the submission history before resuming");
                return "verified";
        }
    }

    /// <summary>
    /// Determines acceptance from the account state (the day page shows accepted answers) without POSTing, so an
    /// answer whose outcome is unknown is never re-sent without first checking whether it already counted.
    /// </summary>
    public async Task<string> ReconcileAsync(Submission? submission, CancellationToken ct)
    {
        var page = await S.S.Aoc.DayPageAsync(S.Year, S.Day, refresh: true, ct);
        var accepted = page.CompletedAnswers.ContainsKey(S.Part);
        if (submission is not null)
            await S.S.Store.Ledger.UpsertSubmissionAsync(submission with
            {
                Outcome = accepted ? SubmissionOutcome.Correct : SubmissionOutcome.Unexpected,
                Message = (submission.Message ?? "") + (accepted ? " | reconciled: accepted" : " | reconciled: not accepted"),
                RespondedAt = S.S.Clock.UtcNow,
            }, ct);
        await S.EmitAsync("submit.reconciled", "Reconciled with account state: " + (accepted ? "part is complete" : "part is not complete"),
            accepted ? "success" : "warning", AgentIds.System, new { verified = accepted }, ct: ct);
        if (accepted)
        {
            await VerifiedAsync(page.CompletedAnswers[S.Part] ?? submission?.Answer ?? "", ct);
            return "verified";
        }
        // Not accepted: the answer may never have arrived. Allow one re-submission after a safety wait.
        await S.S.Gate.SetCooldownAsync(S.Year, S.S.Clock.UtcNow.AddSeconds(ReconcileWaitSeconds), ct);
        return "retry_same";
    }

    private async Task VerifiedAsync(string answer, CancellationToken ct)
    {
        await S.SaveAsync(p => p with { AcceptedAnswer = answer }, ct);
        await S.SetStateAsync(PartState.VERIFIED, null, ct);
        await S.EmitAsync("submit.result", "Advent of Code accepted the answer", "success", AgentIds.System, new { outcome = "CORRECT", answer }, ct: ct);
        var account = await S.S.Store.Ledger.GetAccountAsync(S.Year, ct);
        var stars = new Dictionary<string, int>(account.Stars);
        var key = S.Day.ToString(System.Globalization.CultureInfo.InvariantCulture);
        stars[key] = Math.Max(stars.GetValueOrDefault(key), S.Part);
        await S.S.Store.Ledger.UpsertAccountAsync(account with { Stars = stars }, ct);
        var verified = (await S.S.Store.Parts.ListPartsAsync(S.Run.Id, ct)).Count(p => p.State == PartState.VERIFIED);
        await S.EmitAsync("star.earned", $"⭐ Day {S.Day:D2} Part {S.Part} verified by Advent of Code", "success", AgentIds.System,
            new { stars_total = verified }, PartSession.Patch(("replace", "/stars", verified)), ct);
    }

    private static string Cap(string? s) => s is null ? "" : s.Length <= 300 ? s : s[..300];
}
