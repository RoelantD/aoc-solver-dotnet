using System.Collections.Concurrent;
using Aps.Agent.Abstractions;
using Aps.Agent.Agents;
using Aps.Agent.Aoc;
using Aps.Agent.Domain;
using Aps.Agent.Execution;
using Aps.Agent.Persistence;
using Aps.Agent.Verification;
using Aps.Agent.Workflow;

namespace Aps.Agent.Protocol;

/// <summary>
/// Owns the runs executing in this process. A run is started detached from any request, so a client disconnect never
/// cancels it. Pause/stop requests are written to the run document by whichever instance received them (the dashboard
/// may reach a different hosted session than the one executing); a watcher applies them here when this instance
/// holds the lease.
/// </summary>
public sealed class RunManager(
    IApsStore store,
    EventLog events,
    IAgentTeam team,
    ICodeExecutor sandbox,
    AocClient aoc,
    IClock clock,
    ILogger<RunManager> logger,
    IHostApplicationLifetime lifetime)
{
    private sealed record Active(Task Execution, RunControl Control);

    private readonly ConcurrentDictionary<string, Active> _active = new();

    /// <summary>Lease owner id: the hosted session id when available, plus a per-process guid.</summary>
    public string Owner { get; } =
        $"{Environment.GetEnvironmentVariable("FOUNDRY_AGENT_SESSION_ID") ?? Environment.MachineName}/{Guid.NewGuid():n}";

    /// <summary>Acquires the lease and starts executing the run in the background. False when another owner holds it.</summary>
    public async Task<bool> TryStartAsync(string runId, CancellationToken ct)
    {
        if (_active.ContainsKey(runId)) return true;
        var lease = new RunLease(store.Runs, Owner, clock);
        if (!await lease.TryAcquireAsync(runId, ct)) return false;

        var control = new RunControl();
        var services = new SolverServices(store, events, lease, new AgentRunner(team), team, sandbox, aoc,
            new SubmissionGate(store.Ledger, clock), control, clock);
        var stopping = lifetime.ApplicationStopping;
        var execution = Task.Run(async () =>
        {
            using var watcher = CancellationTokenSource.CreateLinkedTokenSource(stopping);
            var watch = WatchControlAsync(runId, control, watcher.Token);
            try
            {
                var status = await new RunOrchestrator(services, runId, logger).RunAsync(stopping);
                logger.LogInformation("Run {RunId} ended with {Status}", runId, status);
            }
            catch (Exception e)
            {
                logger.LogError(e, "Run {RunId} crashed outside the orchestrator", runId);
            }
            finally
            {
                await watcher.CancelAsync();
                try { await watch; } catch (OperationCanceledException) { }
                _active.TryRemove(runId, out _);
            }
        }, CancellationToken.None);
        _active[runId] = new Active(execution, control);
        return true;
    }

    /// <summary>Records a pause or stop request; applied by the leaseholder within about a second.</summary>
    public async Task RequestControlAsync(string runId, string request, CancellationToken ct)
    {
        await RunDocument.UpdateAsync(store.Runs, runId, r => r with { ControlRequest = request }, ct);
        if (_active.TryGetValue(runId, out var active)) Apply(active.Control, request);
    }

    private async Task WatchControlAsync(string runId, RunControl control, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
            var run = await store.Runs.GetAsync(runId, ct);
            if (run?.ControlRequest is { } request) Apply(control, request);
        }
    }

    private static void Apply(RunControl control, string request)
    {
        if (request == "stop") control.RequestStop();
        else if (request == "pause") control.RequestPause();
    }
}
