using Aps.Agent.Domain;
using Aps.Agent.Persistence;
using Aps.Agent.Tests.Fakes;

namespace Aps.Agent.Tests.Persistence;

/// <summary>Run lease: exactly one executing owner per run.</summary>
public sealed class RunLeaseTests
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    private static async Task<(InMemoryStore Store, LeaseClock Clock)> SetupAsync()
    {
        var store = new InMemoryStore();
        await store.Runs.CreateAsync(new Run { Id = "r1", Year = 2024, DayFrom = 1, DayTo = 1, Status = RunStatus.Running }, Ct);
        return (store, new LeaseClock());
    }

    [Fact]
    public async Task Acquire_succeeds_when_free_and_sets_expiry()
    {
        var (store, clock) = await SetupAsync();
        var lease = new RunLease(store.Runs, "owner-a", clock);

        Assert.True(await lease.TryAcquireAsync("r1", Ct));

        var run = await store.Runs.GetAsync("r1", Ct);
        Assert.Equal("owner-a", run!.Lease!.Owner);
        Assert.Equal(clock.UtcNow + RunLease.Ttl, run.Lease.ExpiresAt);
    }

    [Fact]
    public async Task Acquire_fails_while_another_owner_holds_a_live_lease()
    {
        var (store, clock) = await SetupAsync();
        Assert.True(await new RunLease(store.Runs, "owner-a", clock).TryAcquireAsync("r1", Ct));

        Assert.False(await new RunLease(store.Runs, "owner-b", clock).TryAcquireAsync("r1", Ct));
    }

    [Fact]
    public async Task Acquire_takes_over_an_expired_lease()
    {
        var (store, clock) = await SetupAsync();
        Assert.True(await new RunLease(store.Runs, "owner-a", clock).TryAcquireAsync("r1", Ct));
        clock.Advance(RunLease.Ttl + TimeSpan.FromSeconds(1));

        var b = new RunLease(store.Runs, "owner-b", clock);
        Assert.True(await b.TryAcquireAsync("r1", Ct));
        Assert.True(b.TookOver);
        Assert.Equal("owner-b", (await store.Runs.GetAsync("r1", Ct))!.Lease!.Owner);
    }

    [Fact]
    public async Task Concurrent_acquire_has_exactly_one_winner()
    {
        var (store, clock) = await SetupAsync();
        var leases = Enumerable.Range(0, 8).Select(i => new RunLease(store.Runs, $"owner-{i}", clock)).ToList();

        var results = await Task.WhenAll(leases.Select(l => l.TryAcquireAsync("r1", Ct)));

        Assert.Equal(1, results.Count(r => r));
    }

    [Fact]
    public async Task Renew_extends_expiry()
    {
        var (store, clock) = await SetupAsync();
        var lease = new RunLease(store.Runs, "owner-a", clock);
        await lease.TryAcquireAsync("r1", Ct);
        clock.Advance(TimeSpan.FromSeconds(20));

        await lease.RenewAsync("r1", Ct);

        Assert.Equal(clock.UtcNow + RunLease.Ttl, (await store.Runs.GetAsync("r1", Ct))!.Lease!.ExpiresAt);
    }

    [Fact]
    public async Task Renew_after_takeover_throws_LeaseLost()
    {
        var (store, clock) = await SetupAsync();
        var a = new RunLease(store.Runs, "owner-a", clock);
        await a.TryAcquireAsync("r1", Ct);
        clock.Advance(RunLease.Ttl + TimeSpan.FromSeconds(1));
        await new RunLease(store.Runs, "owner-b", clock).TryAcquireAsync("r1", Ct);

        await Assert.ThrowsAsync<LeaseLostException>(() => a.RenewAsync("r1", Ct));
    }

    [Fact]
    public async Task Release_clears_the_lease_so_another_owner_can_acquire()
    {
        var (store, clock) = await SetupAsync();
        var a = new RunLease(store.Runs, "owner-a", clock);
        await a.TryAcquireAsync("r1", Ct);

        await a.ReleaseAsync("r1", Ct);

        Assert.Null((await store.Runs.GetAsync("r1", Ct))!.Lease);
        Assert.True(await new RunLease(store.Runs, "owner-b", clock).TryAcquireAsync("r1", Ct));
    }

    [Fact]
    public async Task Updates_by_the_holder_keep_the_lease_and_survive_concurrent_writes()
    {
        var (store, clock) = await SetupAsync();
        var a = new RunLease(store.Runs, "owner-a", clock);
        await a.TryAcquireAsync("r1", Ct);

        var updated = await a.UpdateRunAsync("r1", r => r with { CurrentDay = 3 }, Ct);

        Assert.Equal(3, updated.CurrentDay);
        Assert.Equal("owner-a", updated.Lease!.Owner);
    }
}
