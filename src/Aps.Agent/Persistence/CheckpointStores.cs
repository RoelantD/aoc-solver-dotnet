using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using Microsoft.Azure.Cosmos;

namespace Aps.Agent.Persistence;

/// <summary>
/// Agent Framework workflow checkpoints in the <c>checkpoints</c> container. The framework ships in-memory and
/// file-system stores only; a hosted session's file system does not survive a recycle, so checkpoints live in Cosmos
/// next to the run.
/// </summary>
/// <remarks>
/// Layout: one document per checkpoint, partitioned by run id, so all checkpoints of a run sit in one logical
/// partition. Within it the workflow session id (the part's workflow run id) groups a part's checkpoints, the
/// document id is the checkpoint id, and <c>parentId</c> plus <c>ticks</c> give the chain and its order. The
/// checkpoint value itself is opaque framework JSON.
/// </remarks>
public sealed class CosmosCheckpointStore(Container container, string runId) : JsonCheckpointStore
{
    internal sealed record Doc(
        [property: JsonPropertyName("id")] string Id,
        string RunId,
        string SessionId,
        string? ParentId,
        long Ticks,
        DateTimeOffset CreatedAt,
        JsonElement Value);

    private sealed record IndexRow(
        [property: JsonPropertyName("id")] string Id,
        string? ParentId);

    public override async ValueTask<CheckpointInfo> CreateCheckpointAsync(string sessionId, JsonElement value, CheckpointInfo? parent = null)
    {
        var info = new CheckpointInfo(sessionId, Guid.NewGuid().ToString("n"));
        var now = DateTimeOffset.UtcNow;
        await container.CreateItemAsync(new Doc(info.CheckpointId, runId, sessionId, parent?.CheckpointId, now.UtcTicks, now, value),
            new PartitionKey(runId));
        return info;
    }

    public override async ValueTask<JsonElement> RetrieveCheckpointAsync(string sessionId, CheckpointInfo key)
    {
        var doc = await container.ReadItemAsync<Doc>(key.CheckpointId, new PartitionKey(runId));
        return doc.Resource.SessionId == sessionId
            ? doc.Resource.Value
            : throw new KeyNotFoundException($"checkpoint {key.CheckpointId} belongs to another workflow session");
    }

    public override async ValueTask<IEnumerable<CheckpointInfo>> RetrieveIndexAsync(string sessionId, CheckpointInfo? withParent = null)
    {
        var query = new QueryDefinition("SELECT c.id, c.parentId FROM c WHERE c.sessionId = @s ORDER BY c.ticks")
            .WithParameter("@s", sessionId);
        var rows = new List<IndexRow>();
        using var it = container.GetItemQueryIterator<IndexRow>(query, requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(runId) });
        while (it.HasMoreResults) rows.AddRange(await it.ReadNextAsync());
        return rows.Where(r => withParent is null || r.ParentId == withParent.CheckpointId)
                   .Select(r => new CheckpointInfo(sessionId, r.Id)).ToList();
    }
}

/// <summary>Checkpoints in memory, for local development without Cosmos and for tests. Same ordering as Cosmos.</summary>
public sealed class InMemoryCheckpointStore : JsonCheckpointStore
{
    private readonly object _gate = new();
    private readonly List<(CheckpointInfo Info, string? ParentId, JsonElement Value)> _items = [];

    public int Count { get { lock (_gate) return _items.Count; } }

    public override ValueTask<CheckpointInfo> CreateCheckpointAsync(string sessionId, JsonElement value, CheckpointInfo? parent = null)
    {
        var info = new CheckpointInfo(sessionId, Guid.NewGuid().ToString("n"));
        lock (_gate) _items.Add((info, parent?.CheckpointId, value.Clone()));
        return ValueTask.FromResult(info);
    }

    public override ValueTask<JsonElement> RetrieveCheckpointAsync(string sessionId, CheckpointInfo key)
    {
        lock (_gate)
            return _items.FirstOrDefault(i => i.Info.Equals(key)) is { Info: not null } hit
                ? ValueTask.FromResult(hit.Value)
                : throw new KeyNotFoundException($"checkpoint {key.CheckpointId} not found");
    }

    public override ValueTask<IEnumerable<CheckpointInfo>> RetrieveIndexAsync(string sessionId, CheckpointInfo? withParent = null)
    {
        lock (_gate)
            return ValueTask.FromResult<IEnumerable<CheckpointInfo>>(_items
                .Where(i => i.Info.SessionId == sessionId && (withParent is null || i.ParentId == withParent.CheckpointId))
                .Select(i => i.Info).ToList());
    }
}
