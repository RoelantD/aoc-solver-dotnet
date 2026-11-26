using Aps.Agent.Domain;
using Aps.Agent.Persistence;
using Aps.Agent.Workflow;
using Aps.Agent.Tests.Fakes;
using static Aps.Agent.Tests.Fakes.ScriptedTeam;

namespace Aps.Agent.Tests.Workflow;

/// <summary>
/// The part workflow writes an Agent Framework checkpoint after every superstep, and a new owner of the run
/// resumes the part from the latest one: completed steps are not repeated, the working memory comes back, and a
/// pending approval is asked again with the same request id.
/// </summary>
public sealed class CheckpointTests
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task Every_superstep_of_a_part_writes_a_checkpoint()
    {
        using var h = new RunHarness().AnswerBy((Day1.ExampleInput, "8"), (Day1.RealInput, "18"));
        h.Team.Reply(AgentIds.Analyst, Analysis("Total drift", (Day1.ExampleInput, "8")))
              .Reply(AgentIds.Coder, Program(Day1.GoodCode));
        var run = await h.CreateRunAsync(ct: Ct);

        await h.Control_StopAfterPart1(run.Id, Ct);

        var checkpoints = (await h.EventsAsync(run.Id, Ct)).Where(e => e.Type == "workflow.checkpoint").ToList();
        // One superstep per step. The submit step yields the part's outcome, which ends the part; no checkpoint is
        // needed after that.
        Assert.Equal(["[\"analyst\"]", "[\"verify-examples\"]", "[\"coder\"]", "[\"sandbox-test\"]", "[\"run-input\"]"],
            checkpoints.Select(e => e.Data!.Value.GetProperty("executors").GetRawText()));
        Assert.Equal(checkpoints.Count, checkpoints.Select(e => e.Data!.Value.GetProperty("checkpointId").GetString()).Distinct().Count());
        Assert.True(h.Store.Checkpoints.Count >= checkpoints.Count);
    }

    [Fact]
    public async Task A_part_paused_mid_workflow_resumes_after_the_last_completed_step()
    {
        using var h = new RunHarness().AnswerBy((Day1.ExampleInput, "8"), (Day1.RealInput, "18"));
        h.Team.Reply(AgentIds.Analyst, Analysis("Total drift", (Day1.ExampleInput, "8")))
              .Reply(AgentIds.Coder, Program(Day1.GoodCode));
        var run = await h.CreateRunAsync(ct: Ct);

        // Instance A: the Coder finishes revision 1, then the run is paused before the sandbox test runs.
        h.Events.Appended += e => { if (e.Type == "agent.code") h.Control.RequestPause("paused for the test"); };
        Assert.Equal(RunStatus.Paused, await h.RunAsync(run.Id, Ct));
        var pausedAt = (await h.EventsAsync(run.Id, Ct)).Count;

        // Instance B continues the run. The scripted models have no replies left: any repeated model call fails the test.
        var status = await new RunHarnessView(h, StopsAfterPart1(h)).RunAsync(run.Id, "instance-b", Ct);

        Assert.Equal(RunStatus.Stopped, status);
        Assert.Equal(["18"], h.Site.Posts.Select(p => p.Answer));
        Assert.Single(h.Team.Prompts, p => p.Agent == AgentIds.Analyst);
        Assert.Single(h.Team.Prompts, p => p.Agent == AgentIds.Coder);
        var after = (await h.EventsAsync(run.Id, Ct)).Skip(pausedAt).ToList();
        Assert.Contains(after, e => e.Type == "workflow.resumed");
        // The verified examples came back from the checkpoint, so the sandbox test ran them.
        Assert.Contains(after, e => e.Type == "test.result" && e.Message.StartsWith("All example tests passed", StringComparison.Ordinal));
        Assert.Equal(PartState.VERIFIED, (await h.Store.Parts.GetPartAsync(run.Id, 1, 1, Ct))!.State);
    }

    [Fact]
    public async Task A_part_paused_at_the_approval_asks_again_with_the_same_request_and_keeps_the_decision()
    {
        using var h = new RunHarness().AnswerBy((Day1.ExampleInput, "8"), (Day1.RealInput, "18"));
        h.Team.Reply(AgentIds.Analyst, Analysis("Total drift", (Day1.ExampleInput, "8")))
              .Reply(AgentIds.Coder, Program(Day1.GoodCode));
        var run = await h.CreateRunAsync(config: new RunConfig { AocMinIntervalS = 1, ApprovalMode = ApprovalMode.Manual }, ct: Ct);

        // Instance A waits for the approval, then its session goes away.
        h.Events.Appended += e => { if (e.Type == "approval.requested") h.Control.RequestPause("session recycled"); };
        Assert.Equal(RunStatus.Paused, await h.RunAsync(run.Id, Ct));
        var pending = Assert.Single((await h.Store.Runs.GetAsync(run.Id, Ct))!.PendingApprovals);
        Assert.Empty(h.Site.Posts);

        // The operator approves while no instance runs the part.
        await RunDocument.UpdateAsync(h.Store.Runs, run.Id, r => r with
        {
            PendingApprovals = [.. r.PendingApprovals.Select(a => a with { State = ApprovalState.Approved, DecidedBy = "operator", DecidedAt = DateTimeOffset.UtcNow })],
        }, Ct);

        var status = await new RunHarnessView(h, StopsAfterPart1(h)).RunAsync(run.Id, "instance-b", Ct);

        Assert.Equal(RunStatus.Stopped, status);
        Assert.Equal(["18"], h.Site.Posts.Select(p => p.Answer)); // submitted once, with the approved answer
        var events = await h.EventsAsync(run.Id, Ct);
        Assert.Single(events, e => e.Type == "approval.requested"); // the operator was not asked twice
        Assert.Contains(events, e => e.Type == "workflow.resumed");
        // After the resume the dashboard shows the part waiting for the approval again, not FETCHING.
        var resumedAt = events.First(e => e.Type == "workflow.resumed").Seq;
        Assert.Contains(events, e => e.Seq > resumedAt && e.Type == "part.state" && e.Message.EndsWith("WAITING_FOR_APPROVAL", StringComparison.Ordinal));
        Assert.Single(h.Team.Prompts, p => p.Agent == AgentIds.Coder);
        Assert.Empty((await h.Store.Runs.GetAsync(run.Id, Ct))!.PendingApprovals);
        var decided = Assert.Single(events, e => e.Type == "approval.decided");
        Assert.Equal(pending.RequestId, decided.Data!.Value.GetProperty("requestId").GetString()); // same request after the resume
        Assert.Equal("approved", decided.Data!.Value.GetProperty("decision").GetString());
    }

    /// <summary>A control for instance B that stops the run once Part 1 is terminal (Part 2 is not scripted).</summary>
    private static RunControl StopsAfterPart1(RunHarness h)
    {
        var control = new RunControl();
        h.Events.Appended += e => { if (e is { Part: 1, Type: "star.earned" or "part.failed" }) control.RequestStop(); };
        return control;
    }
}
