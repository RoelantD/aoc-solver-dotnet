using System.Collections.Concurrent;
using Aps.Agent.Abstractions;
using Aps.Agent.Domain;

namespace Aps.Agent.Persistence;

/// <summary>
/// In-memory implementation of every store, with the same concurrency semantics as Cosmos DB
/// (ETag checks, unique ids). Used by tests and by local development when <c>COSMOS_ENDPOINT</c>
/// is not set. Not durable: never used in the hosted environment.
/// </summary>
public sealed class InMemoryStore : IApsStore, IRunStore, IPartStore, ILedgerStore, IEventStore, IPuzzleCache
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Run> _runs = [];
    private readonly Dictionary<(string, string), object> _parts = [];
    private readonly Dictionary<(int, string), object> _ledger = [];
    private readonly ConcurrentDictionary<string, SortedDictionary<long, DomainEvent>> _events = new();
    private readonly ConcurrentDictionary<(int, int, string), string> _cache = new();
    private long _etag;

    public IRunStore Runs => this;
    public IPartStore Parts => this;
    public ILedgerStore Ledger => this;
    public IEventStore Events => this;
    public IPuzzleCache Cache => this;

    /// <summary>One checkpoint store for every run; workflow session ids are unique across runs.</summary>
    public InMemoryCheckpointStore Checkpoints { get; } = new();

    public Microsoft.Agents.AI.Workflows.Checkpointing.JsonCheckpointStore CheckpointsFor(string runId) => Checkpoints;

    private string NextETag() => Interlocked.Increment(ref _etag).ToString(System.Globalization.CultureInfo.InvariantCulture);

    // ---- runs
    public Task<Run?> GetAsync(string runId, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(_runs.GetValueOrDefault(runId));
    }

    public Task<Run> CreateAsync(Run run, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_runs.ContainsKey(run.Id)) throw new ConcurrencyException($"run {run.Id} exists");
            var stored = run with { ETag = NextETag() };
            _runs[run.Id] = stored;
            return Task.FromResult(stored);
        }
    }

    public Task<Run> ReplaceAsync(Run run, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_runs.TryGetValue(run.Id, out var current) || current.ETag != run.ETag)
                throw new ConcurrencyException($"run {run.Id} changed");
            var stored = run with { ETag = NextETag() };
            _runs[run.Id] = stored;
            return Task.FromResult(stored);
        }
    }

    public Task<IReadOnlyList<Run>> ListAsync(CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult<IReadOnlyList<Run>>(_runs.Values.OrderByDescending(r => r.CreatedAt).ToList());
    }

    // ---- parts
    public Task<PartProgress?> GetPartAsync(string runId, int day, int part, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(_parts.GetValueOrDefault((runId, PartProgress.DocId(day, part))) as PartProgress);
    }

    public Task UpsertPartAsync(PartProgress part, CancellationToken ct = default) => Put(part.RunId, part.Id, part);

    public Task<IReadOnlyList<PartProgress>> ListPartsAsync(string runId, CancellationToken ct = default) =>
        List<PartProgress>(runId, _ => true, p => p.Day * 10 + p.Part);

    public Task UpsertAttemptAsync(Attempt attempt, CancellationToken ct = default) => Put(attempt.RunId, attempt.Id, attempt);

    public Task<IReadOnlyList<Attempt>> ListAttemptsAsync(string runId, int day, int part, CancellationToken ct = default) =>
        List<Attempt>(runId, a => a.Day == day && a.Part == part, a => a.Number);

    public Task UpsertRevisionAsync(CodeRevision revision, CancellationToken ct = default) => Put(revision.RunId, revision.Id, revision);

    public Task<IReadOnlyList<CodeRevision>> ListRevisionsAsync(string runId, int day, int part, CancellationToken ct = default) =>
        List<CodeRevision>(runId, r => r.Day == day && r.Part == part, r => r.Revision);

    public Task AddInvocationAsync(ModelInvocation invocation, CancellationToken ct = default) => Put(invocation.RunId, invocation.Id, invocation);

    private Task Put(string runId, string id, object doc)
    {
        lock (_gate) _parts[(runId, id)] = doc;
        return Task.CompletedTask;
    }

    private Task<IReadOnlyList<T>> List<T>(string runId, Func<T, bool> filter, Func<T, long> order)
    {
        lock (_gate)
        {
            IReadOnlyList<T> items = _parts.Where(kv => kv.Key.Item1 == runId).Select(kv => kv.Value).OfType<T>()
                .Where(filter).OrderBy(order).ToList();
            return Task.FromResult(items);
        }
    }

    // ---- ledger
    public Task<IReadOnlyList<Submission>> ListSubmissionsAsync(int year, int day, int part, CancellationToken ct = default)
    {
        lock (_gate)
        {
            IReadOnlyList<Submission> items = _ledger.Where(kv => kv.Key.Item1 == year).Select(kv => kv.Value).OfType<Submission>()
                .Where(s => s.Day == day && s.Part == part).OrderBy(s => s.SubmittedAt).ToList();
            return Task.FromResult(items);
        }
    }

    public Task UpsertSubmissionAsync(Submission submission, CancellationToken ct = default)
    {
        lock (_gate) _ledger[(submission.Year, submission.Id)] = submission;
        return Task.CompletedTask;
    }

    public Task<AccountState> GetAccountAsync(int year, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(_ledger.GetValueOrDefault((year, "account")) as AccountState ?? new AccountState { Year = year });
    }

    public Task UpsertAccountAsync(AccountState account, CancellationToken ct = default)
    {
        lock (_gate) _ledger[(account.Year, account.Id)] = account;
        return Task.CompletedTask;
    }

    // ---- events
    public Task AppendAsync(DomainEvent evt, CancellationToken ct = default)
    {
        var log = _events.GetOrAdd(evt.RunId, _ => new());
        lock (log)
        {
            if (!log.TryAdd(evt.Seq, evt)) throw new ConcurrencyException($"event {evt.RunId}/{evt.Seq} exists");
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<DomainEvent>> ReadAfterAsync(string runId, long afterSeq, int max = 1000, CancellationToken ct = default)
    {
        if (!_events.TryGetValue(runId, out var log)) return Task.FromResult<IReadOnlyList<DomainEvent>>([]);
        lock (log) return Task.FromResult<IReadOnlyList<DomainEvent>>(log.Values.Where(e => e.Seq > afterSeq).Take(max).ToList());
    }

    public Task<long> MaxSeqAsync(string runId, CancellationToken ct = default)
    {
        if (!_events.TryGetValue(runId, out var log)) return Task.FromResult(0L);
        lock (log) return Task.FromResult(log.Count == 0 ? 0L : log.Keys.Max());
    }

    // ---- cache
    public ValueTask<string?> GetAsync(int year, int day, string kind, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(_cache.GetValueOrDefault((year, day, kind)));

    public ValueTask SetAsync(int year, int day, string kind, string content, CancellationToken cancellationToken = default)
    {
        _cache[(year, day, kind)] = content;
        return ValueTask.CompletedTask;
    }
}
