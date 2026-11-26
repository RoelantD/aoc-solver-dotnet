using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Aps.Agent.Domain;

namespace Aps.Agent.Persistence;

/// <summary>
/// Ordered, persisted domain events per run; the dashboard is rebuilt from them on reconnect.
/// <list type="bullet">
/// <item>Only the leaseholder writes: it opens the log, assigns gapless seq numbers and appends.</item>
/// <item>Any instance can tail: an in-process signal wakes tails in the writer's process; tails in
/// other hosted sessions poll the store.</item>
/// </list>
/// </summary>
public sealed class EventLog(IEventStore store, TimeSpan? pollInterval = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly TimeSpan _poll = pollInterval ?? TimeSpan.FromSeconds(1);
    private readonly ConcurrentDictionary<string, long> _nextSeq = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _writeLocks = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _signals = new();

    /// <summary>Raised after an event is persisted (used for telemetry and tests).</summary>
    public event Action<DomainEvent>? Appended;

    /// <summary>Prepares this instance to write <paramref name="runId"/>, continuing after the persisted max seq.</summary>
    public async Task OpenForWriteAsync(string runId, CancellationToken ct = default) =>
        _nextSeq[runId] = await store.MaxSeqAsync(runId, ct) + 1;

    /// <summary>Stops writing <paramref name="runId"/> from this instance (lease released or lost).</summary>
    public void CloseForWrite(string runId) => _nextSeq.TryRemove(runId, out _);

    /// <summary>Appends an event with the next seq.</summary>
    /// <exception cref="ConcurrencyException">Another writer already used this seq.</exception>
    public async Task<DomainEvent> AppendAsync(string runId, NewEvent e, CancellationToken ct = default)
    {
        var gate = _writeLocks.GetOrAdd(runId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (!_nextSeq.TryGetValue(runId, out var seq))
                throw new InvalidOperationException($"event log for run {runId} is not open for writing");

            var evt = new DomainEvent
            {
                RunId = runId,
                Seq = seq,
                Ts = DateTimeOffset.UtcNow,
                Type = e.Type,
                Level = e.Level,
                Day = e.Day,
                Part = e.Part,
                Agent = e.Agent,
                Message = e.Message,
                Data = e.Data switch
                {
                    null => null,
                    JsonElement j => j,
                    var o => JsonSerializer.SerializeToElement(o, o.GetType(), Json),
                },
                Patch = e.StatePatch,
            };
            await store.AppendAsync(evt, ct);
            _nextSeq[runId] = seq + 1;
            Signal(runId);
            Appended?.Invoke(evt);
            return evt;
        }
        finally
        {
            gate.Release();
        }
    }

    public Task<IReadOnlyList<DomainEvent>> ReadAfterAsync(string runId, long afterSeq, CancellationToken ct = default) =>
        store.ReadAfterAsync(runId, afterSeq, ct: ct);

    /// <summary>
    /// Yields every event after <paramref name="afterSeq"/>, then follows live events. Yields <c>null</c> as a heartbeat
    /// after <paramref name="heartbeat"/> without events. Ends once <paramref name="isFinished"/> reports true and all
    /// persisted events have been drained; otherwise runs until cancelled.
    /// </summary>
    public async IAsyncEnumerable<DomainEvent?> TailAsync(string runId, long afterSeq, TimeSpan heartbeat,
        [EnumeratorCancellation] CancellationToken ct = default, Func<Task<bool>>? isFinished = null)
    {
        var last = afterSeq;
        var idleSince = DateTimeOffset.UtcNow;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var signal = _signals.GetOrAdd(runId, _ => NewSignal()).Task;
            var batch = await store.ReadAfterAsync(runId, last, ct: ct);
            foreach (var evt in batch)
            {
                last = evt.Seq;
                yield return evt;
            }
            if (batch.Count > 0)
            {
                idleSince = DateTimeOffset.UtcNow;
                continue;
            }

            if (isFinished is not null && await isFinished())
            {
                // Drain anything written between the read and the finished check, then stop.
                foreach (var evt in await store.ReadAfterAsync(runId, last, ct: ct)) yield return evt;
                yield break;
            }

            var untilHeartbeat = heartbeat - (DateTimeOffset.UtcNow - idleSince);
            if (untilHeartbeat <= TimeSpan.Zero)
            {
                idleSince = DateTimeOffset.UtcNow;
                yield return null;
                continue;
            }

            var wait = untilHeartbeat < _poll ? untilHeartbeat : _poll;
            await Task.WhenAny(signal, Task.Delay(wait, ct));
        }
    }

    private void Signal(string runId)
    {
        if (_signals.TryRemove(runId, out var tcs)) tcs.TrySetResult();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
