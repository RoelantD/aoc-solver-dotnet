using Aps.Agent.Domain;
using Aps.Agent.Persistence;

namespace Aps.Agent.Tests.Persistence;

/// <summary>Event log: gapless ordering, exact replay after a seq, single writer, tail wake-up and polling.</summary>
public sealed class EventLogTests
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    private static async Task<(InMemoryStore Store, EventLog Log)> NewLogAsync(string runId = "run-1")
    {
        var store = new InMemoryStore();
        var log = new EventLog(store.Events);
        await log.OpenForWriteAsync(runId, Ct);
        return (store, log);
    }

    [Fact]
    public async Task Append_assigns_gapless_increasing_seq()
    {
        var (store, log) = await NewLogAsync();
        for (var i = 0; i < 5; i++) await log.AppendAsync("run-1", new NewEvent("agent.step", $"step {i}"), Ct);

        var events = await store.Events.ReadAfterAsync("run-1", 0, ct: Ct);
        Assert.Equal([1L, 2, 3, 4, 5], events.Select(e => e.Seq));
    }

    [Fact]
    public async Task ReadAfter_returns_exactly_the_later_events_in_order()
    {
        var (_, log) = await NewLogAsync();
        for (var i = 0; i < 10; i++) await log.AppendAsync("run-1", new NewEvent("agent.step", $"step {i}"), Ct);

        var after = await log.ReadAfterAsync("run-1", 6, Ct);
        Assert.Equal([7L, 8, 9, 10], after.Select(e => e.Seq));
    }

    [Fact]
    public async Task Resuming_writer_continues_after_persisted_max_seq()
    {
        var (store, log) = await NewLogAsync();
        await log.AppendAsync("run-1", new NewEvent("a", "1"), Ct);
        await log.AppendAsync("run-1", new NewEvent("a", "2"), Ct);

        var resumed = new EventLog(store.Events);
        await resumed.OpenForWriteAsync("run-1", Ct);
        var evt = await resumed.AppendAsync("run-1", new NewEvent("a", "3"), Ct);

        Assert.Equal(3, evt.Seq);
    }

    [Fact]
    public async Task Second_writer_with_stale_seq_is_rejected()
    {
        var (store, log) = await NewLogAsync();
        var stale = new EventLog(store.Events);
        await stale.OpenForWriteAsync("run-1", Ct); // both believe next seq is 1
        await log.AppendAsync("run-1", new NewEvent("a", "first"), Ct);

        await Assert.ThrowsAsync<ConcurrencyException>(() => stale.AppendAsync("run-1", new NewEvent("a", "dup"), Ct));
    }

    [Fact]
    public async Task Append_without_open_writer_fails()
    {
        var log = new EventLog(new InMemoryStore().Events);
        await Assert.ThrowsAsync<InvalidOperationException>(() => log.AppendAsync("run-x", new NewEvent("a", "b"), Ct));
    }

    [Fact]
    public async Task Tail_wakes_on_in_process_append()
    {
        var (_, log) = await NewLogAsync();
        await log.AppendAsync("run-1", new NewEvent("a", "1"), Ct);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(TimeSpan.FromSeconds(5));
        var seen = new List<long>();
        var tail = Task.Run(async () =>
        {
            await foreach (var item in log.TailAsync("run-1", 0, heartbeat: TimeSpan.FromSeconds(30), cts.Token))
            {
                if (item is { } e) seen.Add(e.Seq);
                if (seen.Count == 3) break;
            }
        }, Ct);

        await log.AppendAsync("run-1", new NewEvent("a", "2"), Ct);
        await log.AppendAsync("run-1", new NewEvent("a", "3"), Ct);
        await tail;

        Assert.Equal([1L, 2, 3], seen);
    }

    [Fact]
    public async Task Tail_polls_the_store_for_events_written_by_another_instance()
    {
        var (store, writer) = await NewLogAsync();
        var reader = new EventLog(store.Events, pollInterval: TimeSpan.FromMilliseconds(50)); // other session

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(TimeSpan.FromSeconds(5));
        var seen = new List<long>();
        var tail = Task.Run(async () =>
        {
            await foreach (var item in reader.TailAsync("run-1", 0, heartbeat: TimeSpan.FromSeconds(30), cts.Token))
            {
                if (item is { } e) seen.Add(e.Seq);
                if (seen.Count == 2) break;
            }
        }, Ct);

        await writer.AppendAsync("run-1", new NewEvent("a", "1"), Ct);
        await writer.AppendAsync("run-1", new NewEvent("a", "2"), Ct);
        await tail;

        Assert.Equal([1L, 2], seen);
    }

    [Fact]
    public async Task Tail_yields_heartbeat_when_idle()
    {
        var (_, log) = await NewLogAsync();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(TimeSpan.FromSeconds(5));

        var sawHeartbeat = false;
        await foreach (var item in log.TailAsync("run-1", 0, heartbeat: TimeSpan.FromMilliseconds(100), cts.Token))
        {
            if (item is null) { sawHeartbeat = true; break; }
        }

        Assert.True(sawHeartbeat);
    }

    [Fact]
    public async Task Tail_ends_when_stop_condition_is_met_after_draining()
    {
        var (_, log) = await NewLogAsync();
        await log.AppendAsync("run-1", new NewEvent("a", "1"), Ct);
        await log.AppendAsync("run-1", new NewEvent("run.completed", "done"), Ct);

        var seen = new List<long>();
        await foreach (var item in log.TailAsync("run-1", 0, TimeSpan.FromSeconds(30), Ct, isFinished: () => Task.FromResult(true)))
            if (item is { } e) seen.Add(e.Seq);

        Assert.Equal([1L, 2], seen);
    }
}
