using Aps.Agent.Domain;
using Aps.Agent.Persistence;
using Aps.Agent.Workflow;
using Aps.Agent.Tests.Fakes;
using static Aps.Agent.Tests.Fakes.ScriptedTeam;

namespace Aps.Agent.Tests.Workflow;

/// <summary>
/// Restarts and takeovers keep confirmed work, never repeat a submission, and leave exactly one owner.
/// </summary>
public sealed class ResumeTests
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task Resuming_on_another_instance_keeps_verified_parts_and_continues_the_event_sequence()
    {
        using var h = new RunHarness().AnswerBy((Day1.ExampleInput, "8"), (Day1.RealInput, "18"));
        h.Team.Reply(AgentIds.Analyst, Analysis("Total drift", (Day1.ExampleInput, "8")), Analysis("Products", (Day1.ExampleInput, "104")))
              .Reply(AgentIds.Coder, Program(Day1.GoodCode), Program(Day1.Part2Code));
        var run = await h.CreateRunAsync(ct: Ct);

        await h.Control_StopAfterPart1(run.Id, Ct);              // instance A stops after Part 1
        var afterA = await h.EventsAsync(run.Id, Ct);
        Assert.Equal(RunStatus.Stopped, (await h.Store.Runs.GetAsync(run.Id, Ct))!.Status);

        var b = new RunHarnessView(h, new RunControl());          // instance B resumes the same run
        var status = await b.RunAsync(run.Id, "instance-b", Ct);

        Assert.Equal(RunStatus.Completed, status);
        Assert.Equal(["18", "287"], h.Site.Posts.Select(p => p.Answer)); // Part 1 was not submitted again
        var all = await h.EventsAsync(run.Id, Ct);
        Assert.True(all.Count > afterA.Count);
        Assert.Equal(Enumerable.Range(1, all.Count).Select(i => (long)i), all.Select(e => e.Seq)); // gapless across owners
        Assert.Contains(all, e => e.Type == "run.resumed");
        Assert.All(await h.Store.Parts.ListPartsAsync(run.Id, Ct), p => Assert.Equal(PartState.VERIFIED, p.State));
    }

    [Fact]
    public async Task Uncertain_submission_is_reconciled_from_account_state_without_a_second_post()
    {
        using var h = new RunHarness().AnswerBy((Day1.ExampleInput, "8"), (Day1.RealInput, "18"));
        h.Team.Reply(AgentIds.Analyst, Analysis("Total drift", (Day1.ExampleInput, "8")))
              .Reply(AgentIds.Coder, Program(Day1.GoodCode));
        h.Site.DropNextPostAfterProcessing = true; // the site accepts the answer, but the response is lost
        var run = await h.CreateRunAsync(ct: Ct);

        await h.Control_StopAfterPart1(run.Id, Ct);

        Assert.Single(h.Site.Posts);
        var events = await h.EventsAsync(run.Id, Ct);
        Assert.Contains(events, e => e.Type == "submit.reconciled");
        Assert.Equal(PartState.VERIFIED, (await h.Store.Parts.GetPartAsync(run.Id, 1, 1, Ct))!.State);
        var sub = Assert.Single(await h.Store.Ledger.ListSubmissionsAsync(2024, 1, 1, Ct));
        Assert.Equal(Aps.Agent.Aoc.SubmissionOutcome.Correct, sub.Outcome);
    }

    [Fact]
    public async Task Pending_submission_left_by_a_crash_is_reconciled_before_anything_else()
    {
        using var h = new RunHarness().AnswerBy((Day1.ExampleInput, "8"), (Day1.RealInput, "18"));
        var run = await h.CreateRunAsync(ct: Ct);
        // A previous owner crashed after persisting PENDING and after the site processed the POST.
        await h.Store.Ledger.UpsertSubmissionAsync(new Submission
        {
            Id = "crashed", Year = 2024, Day = 1, Part = 1, Answer = "18", RunId = run.Id,
            Outcome = Aps.Agent.Aoc.SubmissionOutcome.Pending, SubmittedAt = h.Clock.UtcNow,
        }, Ct);
        h.Site.Completed[1] = [1];

        await h.Control_StopAfterPart1(run.Id, Ct);

        Assert.Empty(h.Site.Posts); // never re-posted
        Assert.Equal(PartState.VERIFIED, (await h.Store.Parts.GetPartAsync(run.Id, 1, 1, Ct))!.State);
        Assert.Empty(h.Team.Prompts); // no model was needed
    }

    [Fact]
    public async Task Expired_lease_is_taken_over_and_reported()
    {
        using var h = new RunHarness().AnswerBy((Day1.ExampleInput, "8"), (Day1.RealInput, "18"));
        h.Team.Reply(AgentIds.Analyst, Analysis("Total drift", (Day1.ExampleInput, "8")))
              .Reply(AgentIds.Coder, Program(Day1.GoodCode));
        var run = await h.CreateRunAsync(ct: Ct);
        // Instance A acquired the lease, then its session was recycled: it never renews.
        Assert.True(await new RunLease(h.Store.Runs, "instance-a", h.Clock).TryAcquireAsync(run.Id, Ct));
        h.Clock.Advance(RunLease.Ttl + TimeSpan.FromSeconds(1));

        await h.Control_StopAfterPart1(run.Id, Ct);

        var events = await h.EventsAsync(run.Id, Ct);
        Assert.Contains(events, e => e.Type == "run.lease" && e.Data!.Value.GetProperty("event").GetString() == "taken_over");
        Assert.Equal(PartState.VERIFIED, (await h.Store.Parts.GetPartAsync(run.Id, 1, 1, Ct))!.State);
    }

    [Fact]
    public async Task A_live_lease_blocks_a_second_executor()
    {
        using var h = new RunHarness();
        var run = await h.CreateRunAsync(ct: Ct);
        Assert.True(await new RunLease(h.Store.Runs, "instance-a", h.Clock).TryAcquireAsync(run.Id, Ct));

        Assert.False(await h.Services("instance-b").Lease.TryAcquireAsync(run.Id, Ct));
    }
}

/// <summary>A second "instance" over the same store, site and models (different control and lease owner).</summary>
internal sealed class RunHarnessView(RunHarness h, RunControl control)
{
    public async Task<RunStatus> RunAsync(string runId, string owner, CancellationToken ct)
    {
        var svc = h.Services(owner) with { Control = control };
        Assert.True(await svc.Lease.TryAcquireAsync(runId, ct));
        return await new RunOrchestrator(svc, runId).RunAsync(ct);
    }
}
