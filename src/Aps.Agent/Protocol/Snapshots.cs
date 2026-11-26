using System.Text.Json;
using Aps.Agent.Domain;
using Aps.Agent.Persistence;

namespace Aps.Agent.Protocol;

/// <summary>Builds the AG-UI shared-state snapshots.</summary>
public static class Snapshots
{
    public static object RunSummary(Run r) => new
    {
        id = r.Id,
        year = r.Year,
        dayFrom = r.DayFrom,
        dayTo = r.DayTo,
        mode = r.Mode,
        status = r.Status,
        stopReason = r.StopReason,
        approvalMode = r.Config.ApprovalMode,
        autoSubmit = r.AutoSubmit,
        currentDay = r.CurrentDay,
        currentPart = r.CurrentPart,
        lastSeq = r.LastSeq,
        createdAt = r.CreatedAt,
        startedAt = r.StartedAt,
        finishedAt = r.FinishedAt,
        costUsd = r.Usage.Totals.CostUsd,
        leaseHeld = r.Lease is not null,
    };

    /// <summary>Full dashboard state for one run; the paths match the STATE_DELTA patches the agent emits.</summary>
    public static async Task<JsonElement> RunAsync(IApsStore store, Run run, CancellationToken ct)
    {
        var parts = await store.Parts.ListPartsAsync(run.Id, ct);
        var calendar = Enumerable.Range(1, 25).Select(day => Enumerable.Range(1, 2).Select(part =>
            parts.FirstOrDefault(p => p.Day == day && p.Part == part)?.State.ToString() ?? "NOT_STARTED").ToArray()).ToArray();
        var lastSeq = await store.Events.MaxSeqAsync(run.Id, ct);
        var titles = Enumerable.Range(1, 25).Select(day => parts.FirstOrDefault(p => p.Day == day && p.Title is not null)?.Title).ToArray();
        return Serialize(new
        {
            run = RunSummary(run with { LastSeq = lastSeq }),
            calendar,
            titles,
            stars = parts.Count(p => p.State == PartState.VERIFIED),
            activeAgent = run.ActiveAgent,
            pendingApprovals = run.PendingApprovals,
            usage = run.Usage,
        });
    }

    public static async Task<JsonElement> ListAsync(IApsStore store, CancellationToken ct) =>
        Serialize(new { runs = (await store.Runs.ListAsync(ct)).Select(RunSummary) });

    public static async Task<JsonElement> DetailAsync(IApsStore store, Run run, int day, int part, CancellationToken ct) => Serialize(new
    {
        part = await store.Parts.GetPartAsync(run.Id, day, part, ct),
        attempts = await store.Parts.ListAttemptsAsync(run.Id, day, part, ct),
        revisions = await store.Parts.ListRevisionsAsync(run.Id, day, part, ct),
        submissions = (await store.Ledger.ListSubmissionsAsync(run.Year, day, part, ct)).Where(s => s.RunId == run.Id),
        usage = run.Usage,
    });

    public static JsonElement Serialize(object value) => JsonSerializer.SerializeToElement(value, AguiMapping.Json);
}
