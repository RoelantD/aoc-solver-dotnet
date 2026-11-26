using Aps.Agent.Domain;
using Aps.Agent.Tests.Fakes;
using static Aps.Agent.Tests.Fakes.ScriptedTeam;

namespace Aps.Agent.Tests.Workflow;

/// <summary>
/// The real orchestrator and part workflow against scripted models, the fake sandbox and the fake Advent of Code site:
/// progression, repair, escalation and wrong-answer scenarios.
/// </summary>
public sealed class RunWorkflowTests
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    private static RunHarness Day1Harness() =>
        new RunHarness().AnswerBy((Day1.ExampleInput, "8"), (Day1.RealInput, "18"));

    private static void ScriptBothParts(ScriptedTeam team) => team
        .Reply(Domain.AgentIds.Analyst,
            Analysis("Total drift", (Day1.ExampleInput, "8")),
            Analysis("Sum of products", (Day1.ExampleInput, "104")))
        .Reply(Domain.AgentIds.Coder, Program(Day1.GoodCode), Program(Day1.Part2Code, "multiply products"));

    [Fact]
    public async Task Solves_both_parts_with_two_agents_and_server_confirmed_stars()
    {
        using var h = Day1Harness();
        ScriptBothParts(h.Team);
        var run = await h.CreateRunAsync(ct: Ct);

        var status = await h.RunAsync(run.Id, Ct);

        Assert.Equal(RunStatus.Completed, status);
        var parts = await h.Store.Parts.ListPartsAsync(run.Id, Ct);
        Assert.All(parts, p => Assert.Equal(PartState.VERIFIED, p.State));
        Assert.Equal(["18", "287"], h.Site.Posts.Select(p => p.Answer));

        var events = await h.EventsAsync(run.Id, Ct);
        foreach (var part in new[] { 1, 2 })
        {
            var agents = events.Where(e => e.Part == part && e.Type == "agent.active").Select(e => e.Agent).Distinct().ToList();
            Assert.Contains(Domain.AgentIds.Analyst, agents); // at least two distinct agents per solved part
            Assert.Contains(Domain.AgentIds.Coder, agents);
        }
        Assert.Equal(2, events.Count(e => e.Type == "star.earned"));
        Assert.Equal(Enumerable.Range(1, events.Count).Select(i => (long)i), events.Select(e => e.Seq)); // gapless
        Assert.Null((await h.Store.Runs.GetAsync(run.Id, Ct))!.Lease); // released
    }

    [Fact]
    public async Task Repairs_after_a_failing_example_then_verifies()
    {
        using var h = Day1Harness();
        h.Team.Reply(Domain.AgentIds.Analyst, Analysis("Total drift", (Day1.ExampleInput, "8")))
              .Reply(Domain.AgentIds.Coder, Program(Day1.WrongCode), Program(Day1.GoodCode));
        var run = await h.CreateRunAsync(config: new RunConfig { AocMinIntervalS = 1, EscalateAfterFailures = 5 }, ct: Ct);

        await h.Control_StopAfterPart1(run.Id, Ct);

        var p1 = await h.Store.Parts.GetPartAsync(run.Id, 1, 1, Ct);
        Assert.Equal(PartState.VERIFIED, p1!.State);
        Assert.Equal(2, p1.AttemptsUsed);
        var events = await h.EventsAsync(run.Id, Ct);
        Assert.Contains(events, e => e.Type == "test.result" && e.Level == "error");
        Assert.Contains(events, e => e.Type == "agent.repair");
        Assert.DoesNotContain(events, e => e.Type == "agent.handoff");
    }

    [Fact]
    public async Task Hands_off_to_the_escalation_coder_exactly_once_with_a_reason()
    {
        using var h = Day1Harness();
        h.Team.Reply(Domain.AgentIds.Analyst, Analysis("Total drift", (Day1.ExampleInput, "8")))
              .Reply(Domain.AgentIds.Coder, Program(Day1.WrongCode), Program(Day1.WrongCode))
              .Reply(Handover, HandoverReply())
              .Reply(Domain.AgentIds.EscalationCoder, Program(Day1.WrongCode), Program(Day1.GoodCode));
        var run = await h.CreateRunAsync(config: new RunConfig { AocMinIntervalS = 1, EscalateAfterFailures = 2 }, ct: Ct);

        await h.Control_StopAfterPart1(run.Id, Ct);

        var events = await h.EventsAsync(run.Id, Ct);
        var handoff = Assert.Single(events, e => e.Type == "agent.handoff");
        Assert.Equal(Domain.AgentIds.Coder, handoff.Data!.Value.GetProperty("from").GetString());
        Assert.Equal(Domain.AgentIds.EscalationCoder, handoff.Data.Value.GetProperty("to").GetString());
        Assert.Contains("unsuccessful", handoff.Data.Value.GetProperty("reason").GetString());
        Assert.Contains("Lines were not split", handoff.Data.Value.GetProperty("handoverSummary").GetString());
        // The escalation coder works from the handover, not the full previous code.
        var escalationPrompt = h.Team.Prompts.First(p => p.Agent == Domain.AgentIds.EscalationCoder).Prompt;
        Assert.Contains("<handover>", escalationPrompt);
        Assert.DoesNotContain("Most recent code", escalationPrompt);

        var p1 = await h.Store.Parts.GetPartAsync(run.Id, 1, 1, Ct);
        Assert.Equal(PartState.VERIFIED, p1!.State);
        Assert.True(p1.Escalated);
        var usage = (await h.Store.Runs.GetAsync(run.Id, Ct))!.Usage;
        Assert.True(usage.ByAgent.ContainsKey(Domain.AgentIds.EscalationCoder));
        Assert.Equal(usage.Totals.Calls, usage.ByAgent.Values.Sum(u => u.Calls)); // per-agent usage adds up
    }

    [Fact]
    public async Task Model_rate_limit_pauses_without_using_an_attempt_or_handing_off()
    {
        using var h = Day1Harness();
        h.Team.Reply(Domain.AgentIds.Analyst, Analysis("Total drift", (Day1.ExampleInput, "8")))
              .Reply(Domain.AgentIds.Coder, RateLimited());
        var run = await h.CreateRunAsync(config: new RunConfig { AocMinIntervalS = 1, EscalateAfterFailures = 1 }, ct: Ct);

        var status = await h.RunAsync(run.Id, Ct);

        Assert.Equal(RunStatus.Paused, status);
        var p1 = await h.Store.Parts.GetPartAsync(run.Id, 1, 1, Ct);
        Assert.Equal(0, p1!.AttemptsUsed);
        Assert.DoesNotContain(await h.EventsAsync(run.Id, Ct), e => e.Type == "agent.handoff");
        Assert.Empty(h.Site.Posts);
    }

    [Fact]
    public async Task Rejected_answer_is_never_resubmitted_and_the_next_one_is_accepted()
    {
        using var h = new RunHarness();
        h.AnswerBy((Day1.ExampleInput, "8"), (Day1.RealInput, "17"));
        var calls = 0;
        h.SandboxAnswers[Day1.RealInput] = "17";
        h.Team.Reply(Domain.AgentIds.Analyst, Analysis("Total drift", (Day1.ExampleInput, "8")))
              .Reply(Domain.AgentIds.Coder, Program(Day1.GoodCode), Program(Day1.GoodCode + "\n# fixed"));
        h.Site.CooldownSeconds = 0;
        var run = await h.CreateRunAsync(config: new RunConfig { AocMinIntervalS = 1, EscalateAfterFailures = 9 }, ct: Ct);
        h.Events.Appended += e => { if (e.Type == "submit.result" && ++calls == 1) h.SandboxAnswers[Day1.RealInput] = "18"; };

        await h.Control_StopAfterPart1(run.Id, Ct);

        Assert.Equal(["17", "18"], h.Site.Posts.Select(p => p.Answer));
        var subs = await h.Store.Ledger.ListSubmissionsAsync(2024, 1, 1, Ct);
        Assert.Equal(2, subs.Count);
        Assert.Equal(PartState.VERIFIED, (await h.Store.Parts.GetPartAsync(run.Id, 1, 1, Ct))!.State);
    }

    [Fact]
    public async Task Manual_mode_waits_for_approval_and_submits_once()
    {
        using var h = Day1Harness();
        h.Team.Reply(Domain.AgentIds.Analyst, Analysis("Total drift", (Day1.ExampleInput, "8")))
              .Reply(Domain.AgentIds.Coder, Program(Day1.GoodCode));
        var run = await h.CreateRunAsync(config: new RunConfig { AocMinIntervalS = 1, ApprovalMode = ApprovalMode.Manual }, ct: Ct);
        h.Events.Appended += e =>
        {
            if (e.Type != "approval.requested") return;
            var requestId = e.Data!.Value.GetProperty("requestId").GetString();
            _ = Task.Run(async () =>
            {
                Assert.Empty(h.Site.Posts); // nothing was submitted while waiting
                await Aps.Agent.Persistence.RunDocument.UpdateAsync(h.Store.Runs, run.Id, r => r with
                {
                    PendingApprovals = [.. r.PendingApprovals.Select(a => a.RequestId == requestId
                        ? a with { State = ApprovalState.Approved, DecidedBy = "operator", DecidedAt = DateTimeOffset.UtcNow } : a)],
                });
            });
        };

        await h.Control_StopAfterPart1(run.Id, Ct);

        Assert.Equal(["18"], h.Site.Posts.Select(p => p.Answer));
        var events = await h.EventsAsync(run.Id, Ct);
        Assert.Contains(events, e => e.Type == "approval.requested");
        Assert.Empty((await h.Store.Runs.GetAsync(run.Id, Ct))!.PendingApprovals);
    }
}

internal static class HarnessExtensions
{
    /// <summary>Runs until Part 1 of day 1 is terminal, then stops the run (Part 2 is not scripted in these tests).</summary>
    public static async Task Control_StopAfterPart1(this RunHarness h, string runId, CancellationToken ct)
    {
        h.Events.Appended += e =>
        {
            if (e is { Part: 1, Type: "star.earned" or "part.failed" }) h.Control.RequestStop();
        };
        await h.RunAsync(runId, ct);
    }
}
