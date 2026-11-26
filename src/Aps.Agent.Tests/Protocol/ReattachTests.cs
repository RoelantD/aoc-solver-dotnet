using System.Text.Json;
using AGUI.Abstractions;
using AGUI.Server;
using Aps.Agent.Domain;
using Aps.Agent.Protocol;
using Aps.Agent.Tests.Fakes;
using Aps.Agent.Tests.Workflow;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using static Aps.Agent.Tests.Fakes.ScriptedTeam;

namespace Aps.Agent.Tests.Protocol;

/// <summary>
/// The dashboard (AG-UI) protocol. Reattaching after any number of events yields exactly the
/// persisted log after <c>afterSeq</c> — no gaps, no duplicates — and the payload carries no secrets.
/// </summary>
public sealed class ReattachTests
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    private sealed class Lifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    private static RunStreamAgent Agent(RunHarness h) => new(h.Store, h.Events,
        new RunManager(h.Store, h.Events, h.Team, h.Sandbox, h.Aoc, h.Clock, NullLogger<RunManager>.Instance, new Lifetime()),
        h.Clock, "test operator");

    private static async Task<List<Microsoft.Extensions.AI.AIContent>> InvokeAsync(RunStreamAgent agent, string threadId, object props,
        CancellationToken ct, int? take = null)
    {
        var input = new RunAgentInput
        {
            ThreadId = threadId, RunId = Guid.NewGuid().ToString(), Messages = [],
            ForwardedProperties = JsonSerializer.SerializeToElement(props),
        };
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { TypeInfoResolver = AGUIJsonUtilities.DefaultTypeInfoResolver };
        var options = new ChatClientAgentRunOptions(input.ToChatRequestContext(json, null).ChatOptions);
        var items = new List<Microsoft.Extensions.AI.AIContent>();
        await foreach (var update in agent.RunStreamingAsync([], options: options, cancellationToken: ct))
        {
            items.AddRange(update.Contents);
            if (take is { } n && items.Count >= n) break;
        }
        return items;
    }

    private static async Task<(RunHarness H, string RunId)> CompletedRunAsync()
    {
        var h = new RunHarness().AnswerBy((Day1.ExampleInput, "8"), (Day1.RealInput, "18"));
        h.Team.Reply(AgentIds.Analyst, Analysis("Total drift", (Day1.ExampleInput, "8")))
              .Reply(AgentIds.Coder, Program(Day1.GoodCode));
        var run = await h.CreateRunAsync(ct: Ct);
        await h.Control_StopAfterPart1(run.Id, Ct);
        return (h, run.Id);
    }

    private static List<long> Seqs(IEnumerable<Microsoft.Extensions.AI.AIContent> items) =>
        [.. items.OfType<DomainEventContent>().Where(c => c.Event is not null).Select(c => c.Event!.Seq)];

    [Fact]
    public async Task Attach_sends_a_snapshot_then_the_full_log_in_order()
    {
        var (h, runId) = await CompletedRunAsync();
        using var _ = h;
        var persisted = await h.EventsAsync(runId, Ct);

        var items = await InvokeAsync(Agent(h), runId, new { command = "attach", afterSeq = 0 }, Ct);

        Assert.IsType<StateSnapshotContent>(items[0]);
        Assert.Equal(persisted.Select(e => e.Seq), Seqs(items));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(20)]
    public async Task Reattach_after_k_events_returns_exactly_the_rest(int k)
    {
        var (h, runId) = await CompletedRunAsync();
        using var _ = h;
        var persisted = await h.EventsAsync(runId, Ct);

        var first = Seqs(await InvokeAsync(Agent(h), runId, new { command = "attach", afterSeq = 0 }, Ct, take: k + 1));
        var rest = Seqs(await InvokeAsync(Agent(h), runId, new { command = "attach", afterSeq = first.Last() }, Ct));

        Assert.Equal(persisted.Select(e => e.Seq), first.Concat(rest)); // no gaps, no duplicates
    }

    [Fact]
    public async Task Approve_records_the_decision_once_with_the_operator_name()
    {
        using var h = new RunHarness();
        var run = await h.CreateRunAsync(ct: Ct);
        await Aps.Agent.Persistence.RunDocument.UpdateAsync(h.Store.Runs, run.Id, r => r with
        {
            PendingApprovals = [new ApprovalRequest { RequestId = "req-1", Day = 1, Part = 1, Answer = "18" }],
        }, Ct);
        var agent = Agent(h);

        await InvokeAsync(agent, run.Id, new { command = "approve", args = new { requestId = "req-1" } }, Ct);
        await InvokeAsync(agent, run.Id, new { command = "reject", args = new { requestId = "req-1" } }, Ct); // ignored: already decided

        var approval = Assert.Single((await h.Store.Runs.GetAsync(run.Id, Ct))!.PendingApprovals);
        Assert.Equal(ApprovalState.Approved, approval.State);
        Assert.Equal("test operator", approval.DecidedBy);
    }

    [Fact]
    public async Task Start_for_an_unfinished_year_is_refused()
    {
        using var h = new RunHarness();
        var items = await InvokeAsync(Agent(h), "", new { command = "start", args = new { year = 2026 } }, Ct);

        var error = Assert.Single(items.OfType<DomainEventContent>());
        Assert.Equal("command.error", error.Event!.Type);
        Assert.Equal("year_active", error.Event.Data!.Value.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Stream_payloads_never_contain_the_session_cookie_or_personal_input()
    {
        var (h, runId) = await CompletedRunAsync();
        using var _ = h;
        var items = await InvokeAsync(Agent(h), runId, new { command = "attach", afterSeq = 0 }, Ct);

        var wire = string.Join("\n", items.SelectMany(c => AguiMapping.Map(c) ?? []).Select(e => JsonSerializer.Serialize(e, e.GetType())));

        Assert.DoesNotContain("test-session-cookie", wire);
        Assert.DoesNotContain(Day1.RealInput.Trim(), wire);        // the personal input never leaves the store
        Assert.DoesNotContain("12 20", wire);
    }
}
