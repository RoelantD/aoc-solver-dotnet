using Aps.Agent.Abstractions;
using Aps.Agent.Domain;

namespace Aps.Agent.Persistence;

/// <summary>This instance no longer holds the run's lease; it must stop executing the run at once.</summary>
public sealed class LeaseLostException(string runId) : Exception($"lease for run {runId} was lost");

/// <summary>
/// Durable lease that makes exactly one instance execute a run, even when several instances of the
/// agent are running. Stored on the run document and changed only through ETag-conditioned
/// replaces, so two instances can never both believe they acquired it. Other writers (for example an approval
/// recorded by another hosted session) also change the ETag, so conflicts are retried and only a different
/// owner counts as a lost lease. An owner that stops renewing loses the lease after <see cref="Ttl"/>.
/// </summary>
public sealed class RunLease(IRunStore runs, string owner, IClock? clock = null)
{
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan RenewInterval = TimeSpan.FromSeconds(20);
    private const int MaxConflictRetries = 8;

    private readonly IClock _clock = clock ?? SystemClock.Instance;

    public string Owner => owner;

    /// <summary>True when the last successful acquire replaced another owner's expired lease.</summary>
    public bool TookOver { get; private set; }

    public async Task<bool> TryAcquireAsync(string runId, CancellationToken ct = default)
    {
        var run = await runs.GetAsync(runId, ct) ?? throw new InvalidOperationException($"run {runId} not found");
        var now = _clock.UtcNow;
        if (run.Lease is { } held && held.Owner != owner && held.ExpiresAt > now) return false;
        try
        {
            await runs.ReplaceAsync(run with { Lease = new Lease(owner, now, now + Ttl) }, ct);
            TookOver = run.Lease is { } previous && previous.Owner != owner;
            return true;
        }
        catch (ConcurrencyException)
        {
            return false; // someone else changed the run concurrently; the caller may retry later
        }
    }

    /// <exception cref="LeaseLostException">Another owner holds the lease.</exception>
    public Task RenewAsync(string runId, CancellationToken ct = default) =>
        UpdateRunAsync(runId, r => r, ct);

    /// <summary>Applies <paramref name="mutate"/> to the run while holding the lease, renewing it.</summary>
    /// <exception cref="LeaseLostException">Another owner holds the lease.</exception>
    public async Task<Run> UpdateRunAsync(string runId, Func<Run, Run> mutate, CancellationToken ct = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            var run = await runs.GetAsync(runId, ct) ?? throw new LeaseLostException(runId);
            if (run.Lease?.Owner != owner) throw new LeaseLostException(runId);
            var now = _clock.UtcNow;
            var next = mutate(run) with { Lease = run.Lease with { ExpiresAt = now + Ttl }, ETag = run.ETag };
            try
            {
                return await runs.ReplaceAsync(next, ct);
            }
            catch (ConcurrencyException) when (attempt < MaxConflictRetries)
            {
                // Another writer changed the document; re-read and check ownership again.
            }
        }
    }

    public async Task ReleaseAsync(string runId, CancellationToken ct = default)
    {
        try
        {
            await UpdateRunAsync(runId, r => r, ct);
            await RunDocument.UpdateAsync(runs, runId, r => r.Lease?.Owner == owner ? r with { Lease = null } : r, ct);
        }
        catch (LeaseLostException)
        {
            // Already gone; nothing to release.
        }
    }
}

/// <summary>Lease-independent run updates (control requests, approval decisions) with conflict retries.</summary>
public static class RunDocument
{
    public static async Task<Run> UpdateAsync(IRunStore runs, string runId, Func<Run, Run> mutate, CancellationToken ct = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            var run = await runs.GetAsync(runId, ct) ?? throw new KeyNotFoundException($"run {runId} not found");
            try
            {
                return await runs.ReplaceAsync(mutate(run) with { ETag = run.ETag }, ct);
            }
            catch (ConcurrencyException) when (attempt < 8)
            {
            }
        }
    }
}
