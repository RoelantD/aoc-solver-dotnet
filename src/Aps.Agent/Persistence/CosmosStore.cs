using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aps.Agent.Abstractions;
using Aps.Agent.Domain;
using Azure.Core;
using Microsoft.Azure.Cosmos;

namespace Aps.Agent.Persistence;

/// <summary>
/// Cosmos DB for NoSQL implementation. Entra auth only (local auth is disabled on the account); the
/// hosted agent uses its own identity, a developer uses <c>az login</c>.
/// </summary>
public sealed class CosmosStore : IApsStore, IRunStore, IPartStore, ILedgerStore, IEventStore, IPuzzleCache, IDisposable
{
    public const string DatabaseName = "aps";

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly CosmosClient _client;
    private Container _runs = null!, _parts = null!, _ledger = null!, _events = null!, _cache = null!, _checkpoints = null!;

    public CosmosStore(string endpoint, TokenCredential credential)
    {
        _client = new CosmosClient(endpoint, credential, new CosmosClientOptions
        {
            UseSystemTextJsonSerializerWithOptions = Json,
            ApplicationName = "aps-solver",
        });
    }

    public IRunStore Runs => this;
    public IPartStore Parts => this;
    public ILedgerStore Ledger => this;
    public IEventStore Events => this;
    public IPuzzleCache Cache => this;

    public Microsoft.Agents.AI.Workflows.Checkpointing.JsonCheckpointStore CheckpointsFor(string runId) => new CosmosCheckpointStore(_checkpoints, runId);

    /// <summary>Opens the database and containers. Database and containers are created by infra, not here.</summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var db = _client.GetDatabase(DatabaseName);
        _runs = db.GetContainer("runs");
        _parts = db.GetContainer("parts");
        _ledger = db.GetContainer("ledger");
        _events = db.GetContainer("events");
        _cache = db.GetContainer("cache");
        _checkpoints = db.GetContainer("checkpoints");
        await _runs.ReadContainerAsync(cancellationToken: ct); // fail fast on auth/config errors (preflight shows details)
    }

    public void Dispose() => _client.Dispose();

    // ---- runs
    public async Task<Run?> GetAsync(string runId, CancellationToken ct = default)
    {
        try
        {
            var r = await _runs.ReadItemAsync<Run>(runId, new PartitionKey(runId), cancellationToken: ct);
            return r.Resource with { ETag = r.ETag };
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound) { return null; }
    }

    public async Task<Run> CreateAsync(Run run, CancellationToken ct = default)
    {
        try
        {
            var r = await _runs.CreateItemAsync(run, new PartitionKey(run.Id), cancellationToken: ct);
            return r.Resource with { ETag = r.ETag };
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.Conflict) { throw new ConcurrencyException($"run {run.Id} exists"); }
    }

    /// <summary>
    /// Optimistic concurrency: the replace only succeeds when the stored document still has the ETag this
    /// caller read. If anyone wrote in between, Cosmos answers 412 and the caller re-reads and decides again.
    /// </summary>
    public async Task<Run> ReplaceAsync(Run run, CancellationToken ct = default)
    {
        try
        {
            var r = await _runs.ReplaceItemAsync(run, run.Id, new PartitionKey(run.Id),
                new ItemRequestOptions { IfMatchEtag = run.ETag }, ct);
            return r.Resource with { ETag = r.ETag };
        }
        catch (CosmosException e) when (e.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.NotFound)
        {
            throw new ConcurrencyException($"run {run.Id} changed");
        }
    }

    public Task<IReadOnlyList<Run>> ListAsync(CancellationToken ct = default) =>
        Query<Run>(_runs, new QueryDefinition("SELECT * FROM c ORDER BY c.createdAt DESC"), null, ct);

    // ---- parts
    public async Task<PartProgress?> GetPartAsync(string runId, int day, int part, CancellationToken ct = default)
    {
        try
        {
            return (await _parts.ReadItemAsync<PartProgress>(PartProgress.DocId(day, part), new PartitionKey(runId), cancellationToken: ct)).Resource;
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound) { return null; }
    }

    public Task UpsertPartAsync(PartProgress part, CancellationToken ct = default) =>
        _parts.UpsertItemAsync(part, new PartitionKey(part.RunId), cancellationToken: ct);

    public async Task<IReadOnlyList<PartProgress>> ListPartsAsync(string runId, CancellationToken ct = default) =>
        // Sorted in memory: a multi-property ORDER BY needs a composite index.
        [.. (await Query<PartProgress>(_parts, new QueryDefinition("SELECT * FROM c WHERE c.kind = 'part'"), runId, ct))
            .OrderBy(p => p.Day).ThenBy(p => p.Part)];

    public Task UpsertAttemptAsync(Attempt attempt, CancellationToken ct = default) =>
        _parts.UpsertItemAsync(attempt, new PartitionKey(attempt.RunId), cancellationToken: ct);

    public Task<IReadOnlyList<Attempt>> ListAttemptsAsync(string runId, int day, int part, CancellationToken ct = default) =>
        Query<Attempt>(_parts, ByPart("attempt", day, part, "number"), runId, ct);

    public Task UpsertRevisionAsync(CodeRevision revision, CancellationToken ct = default) =>
        _parts.UpsertItemAsync(revision, new PartitionKey(revision.RunId), cancellationToken: ct);

    public Task<IReadOnlyList<CodeRevision>> ListRevisionsAsync(string runId, int day, int part, CancellationToken ct = default) =>
        Query<CodeRevision>(_parts, ByPart("revision", day, part, "revision"), runId, ct);

    public Task AddInvocationAsync(ModelInvocation invocation, CancellationToken ct = default) =>
        _parts.CreateItemAsync(invocation, new PartitionKey(invocation.RunId), cancellationToken: ct);

    private static QueryDefinition ByPart(string kind, int day, int part, string orderBy) =>
        new QueryDefinition($"SELECT * FROM c WHERE c.kind = @kind AND c.day = @day AND c.part = @part ORDER BY c.{orderBy}")
            .WithParameter("@kind", kind).WithParameter("@day", day).WithParameter("@part", part);

    // ---- ledger
    public Task<IReadOnlyList<Submission>> ListSubmissionsAsync(int year, int day, int part, CancellationToken ct = default) =>
        Query<Submission>(_ledger,
            new QueryDefinition("SELECT * FROM c WHERE c.kind = 'submission' AND c.day = @day AND c.part = @part ORDER BY c.submittedAt")
                .WithParameter("@day", day).WithParameter("@part", part),
            null, ct, new PartitionKey(year));

    public Task UpsertSubmissionAsync(Submission submission, CancellationToken ct = default) =>
        _ledger.UpsertItemAsync(submission, new PartitionKey(submission.Year), cancellationToken: ct);

    public async Task<AccountState> GetAccountAsync(int year, CancellationToken ct = default)
    {
        try
        {
            return (await _ledger.ReadItemAsync<AccountState>("account", new PartitionKey(year), cancellationToken: ct)).Resource;
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound) { return new AccountState { Year = year }; }
    }

    public Task UpsertAccountAsync(AccountState account, CancellationToken ct = default) =>
        _ledger.UpsertItemAsync(account, new PartitionKey(account.Year), cancellationToken: ct);

    // ---- events
    public async Task AppendAsync(DomainEvent evt, CancellationToken ct = default)
    {
        try
        {
            await _events.CreateItemAsync(evt, new PartitionKey(evt.RunId), cancellationToken: ct);
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.Conflict)
        {
            throw new ConcurrencyException($"event {evt.RunId}/{evt.Seq} exists");
        }
    }

    public Task<IReadOnlyList<DomainEvent>> ReadAfterAsync(string runId, long afterSeq, int max = 1000, CancellationToken ct = default) =>
        Query<DomainEvent>(_events,
            new QueryDefinition("SELECT TOP @max * FROM c WHERE c.seq > @after ORDER BY c.seq")
                .WithParameter("@max", max).WithParameter("@after", afterSeq),
            runId, ct);

    public async Task<long> MaxSeqAsync(string runId, CancellationToken ct = default)
    {
        var it = _events.GetItemQueryIterator<long?>(new QueryDefinition("SELECT VALUE MAX(c.seq) FROM c"),
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(runId) });
        while (it.HasMoreResults)
            foreach (var v in await it.ReadNextAsync(ct))
                return v ?? 0;
        return 0;
    }

    // ---- cache (pk /year, id "{day}-{kind}")
    private sealed record CacheEntry([property: JsonPropertyName("id")] string Id, int Year, int Day, string Kind, string Content);

    public async ValueTask<string?> GetAsync(int year, int day, string kind, CancellationToken cancellationToken = default)
    {
        try
        {
            return (await _cache.ReadItemAsync<CacheEntry>($"{day:D2}-{kind}", new PartitionKey(year), cancellationToken: cancellationToken)).Resource.Content;
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound) { return null; }
    }

    public async ValueTask SetAsync(int year, int day, string kind, string content, CancellationToken cancellationToken = default) =>
        await _cache.UpsertItemAsync(new CacheEntry($"{day:D2}-{kind}", year, day, kind, content), new PartitionKey(year),
            cancellationToken: cancellationToken);

    private static async Task<IReadOnlyList<T>> Query<T>(Container c, QueryDefinition q, string? runId, CancellationToken ct,
        PartitionKey? pk = null)
    {
        var options = new QueryRequestOptions();
        if (pk is { } p) options.PartitionKey = p;
        else if (runId is not null) options.PartitionKey = new PartitionKey(runId);
        var results = new List<T>();
        var it = c.GetItemQueryIterator<T>(q, requestOptions: options);
        while (it.HasMoreResults) results.AddRange(await it.ReadNextAsync(ct));
        return results;
    }
}
