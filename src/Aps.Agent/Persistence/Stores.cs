using Aps.Agent.Abstractions;
using Aps.Agent.Domain;

namespace Aps.Agent.Persistence;

/// <summary>Thrown when an optimistic-concurrency (ETag) check fails or a document already exists.</summary>
public sealed class ConcurrencyException(string message) : Exception(message);

/// <summary>Runs (container <c>runs</c>). Replace is ETag-conditioned; the returned run carries the new ETag.</summary>
public interface IRunStore
{
    Task<Run?> GetAsync(string runId, CancellationToken ct = default);

    /// <exception cref="ConcurrencyException">A run with this id exists.</exception>
    Task<Run> CreateAsync(Run run, CancellationToken ct = default);

    /// <exception cref="ConcurrencyException">The stored ETag differs from <paramref name="run"/>.ETag.</exception>
    Task<Run> ReplaceAsync(Run run, CancellationToken ct = default);

    Task<IReadOnlyList<Run>> ListAsync(CancellationToken ct = default);
}

/// <summary>Per-run documents (container <c>parts</c>): part progress, attempts, revisions, model invocations.</summary>
public interface IPartStore
{
    Task<PartProgress?> GetPartAsync(string runId, int day, int part, CancellationToken ct = default);

    Task UpsertPartAsync(PartProgress part, CancellationToken ct = default);

    Task<IReadOnlyList<PartProgress>> ListPartsAsync(string runId, CancellationToken ct = default);

    Task UpsertAttemptAsync(Attempt attempt, CancellationToken ct = default);

    Task<IReadOnlyList<Attempt>> ListAttemptsAsync(string runId, int day, int part, CancellationToken ct = default);

    Task UpsertRevisionAsync(CodeRevision revision, CancellationToken ct = default);

    Task<IReadOnlyList<CodeRevision>> ListRevisionsAsync(string runId, int day, int part, CancellationToken ct = default);

    Task AddInvocationAsync(ModelInvocation invocation, CancellationToken ct = default);
}

/// <summary>Account-wide submission ledger and cooldowns (container <c>ledger</c>, pk <c>/year</c>).</summary>
public interface ILedgerStore
{
    Task<IReadOnlyList<Submission>> ListSubmissionsAsync(int year, int day, int part, CancellationToken ct = default);

    Task UpsertSubmissionAsync(Submission submission, CancellationToken ct = default);

    Task<AccountState> GetAccountAsync(int year, CancellationToken ct = default);

    Task UpsertAccountAsync(AccountState account, CancellationToken ct = default);
}

/// <summary>Append-only events (container <c>events</c>, pk <c>/runId</c>).</summary>
public interface IEventStore
{
    /// <exception cref="ConcurrencyException">An event with this seq already exists (a second writer).</exception>
    Task AppendAsync(DomainEvent evt, CancellationToken ct = default);

    Task<IReadOnlyList<DomainEvent>> ReadAfterAsync(string runId, long afterSeq, int max = 1000, CancellationToken ct = default);

    Task<long> MaxSeqAsync(string runId, CancellationToken ct = default);
}

/// <summary>All stores together, as registered in DI.</summary>
public interface IApsStore
{
    IRunStore Runs { get; }
    IPartStore Parts { get; }
    ILedgerStore Ledger { get; }
    IEventStore Events { get; }
    IPuzzleCache Cache { get; }

    /// <summary>Agent Framework workflow checkpoints for the parts of one run (container <c>checkpoints</c>).</summary>
    Microsoft.Agents.AI.Workflows.Checkpointing.JsonCheckpointStore CheckpointsFor(string runId);
}
