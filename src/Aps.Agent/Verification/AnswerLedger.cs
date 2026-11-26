using Aps.Agent.Aoc;
using Aps.Agent.Abstractions;
using Aps.Agent.Domain;
using Aps.Agent.Persistence;

namespace Aps.Agent.Verification;

/// <summary>
/// Rejected answers and bounds for one part. Built from the account-wide submission history, so it holds across
/// runs. A "too high" or "too low" response narrows the range every later candidate must fall in.
/// </summary>
public sealed class AnswerLedger
{
    private readonly HashSet<string> _rejected = [];

    public long? LowerExclusive { get; private set; }
    public long? UpperExclusive { get; private set; }

    public static string Normalize(string? answer) => (answer ?? "").Trim();

    public static AnswerLedger From(IEnumerable<Submission> history)
    {
        var ledger = new AnswerLedger();
        foreach (var s in history) ledger.Record(s.Answer, s.Outcome);
        return ledger;
    }

    public void Record(string answer, SubmissionOutcome outcome)
    {
        if (!PartStates.Rejected.Contains(outcome)) return;
        var a = Normalize(answer);
        _rejected.Add(a);
        if (!long.TryParse(a, out var v)) return;
        if (outcome == SubmissionOutcome.TooHigh) UpperExclusive = UpperExclusive is { } u ? Math.Min(u, v) : v;
        else if (outcome == SubmissionOutcome.TooLow) LowerExclusive = LowerExclusive is { } l ? Math.Max(l, v) : v;
    }

    public (bool Ok, string Reason) Check(string answer)
    {
        var a = Normalize(answer);
        if (a.Length == 0) return (false, "empty answer");
        if (_rejected.Contains(a)) return (false, $"answer {a} was already rejected by Advent of Code");
        if (long.TryParse(a, out var v))
        {
            if (UpperExclusive is { } u && v >= u)
                return (false, $"answer {a} is not below the known upper bound {u} (previously too high)");
            if (LowerExclusive is { } l && v <= l)
                return (false, $"answer {a} is not above the known lower bound {l} (previously too low)");
        }
        return (true, "");
    }

    /// <summary>Text given to the Coder after a rejection; contains no secrets or personal input.</summary>
    public string Feedback()
    {
        var parts = new List<string>();
        if (_rejected.Count > 0) parts.Add("Rejected answers: " + string.Join(", ", _rejected.Order(StringComparer.Ordinal)));
        if (LowerExclusive is { } l) parts.Add($"The answer is greater than {l}.");
        if (UpperExclusive is { } u) parts.Add($"The answer is less than {u}.");
        return string.Join(" ", parts);
    }

    /// <summary>True if the computed answer appears as a literal in the source.</summary>
    public static bool LooksHardcoded(string code, string answer) => HardcodeGuard.LooksHardcoded(code, answer);
}

public enum GateVerdict { Allowed, Duplicate, LimitReached, MustReconcile, Cooldown }

public sealed record GateDecision(GateVerdict Verdict, string Reason = "", Submission? Unresolved = null, DateTimeOffset? CooldownUntil = null);

/// <summary>
/// The persisted checks that every submission passes before the POST: reconcile unresolved submissions first,
/// honour cooldowns, never repeat a rejected answer, cap distinct submissions. Advent of Code penalises wrong
/// answers with growing cooldowns, and resubmitting a known-wrong answer only burns one. A PENDING record is
/// written before the POST, so a crash can never hide a sent answer.
/// </summary>
public sealed class SubmissionGate(ILedgerStore ledger, IClock clock)
{
    public async Task<GateDecision> EvaluateAsync(int year, int day, int part, string answer, int maxSubmissions, CancellationToken ct = default)
    {
        var history = await ledger.ListSubmissionsAsync(year, day, part, ct);
        if (history.FirstOrDefault(s => s.Outcome is SubmissionOutcome.Pending or SubmissionOutcome.Uncertain) is { } open)
            return new(GateVerdict.MustReconcile, "a previous submission has no confirmed outcome", open);

        var account = await ledger.GetAccountAsync(year, ct);
        if (account.CooldownUntil is { } until && until > clock.UtcNow)
            return new(GateVerdict.Cooldown, $"cooldown until {until:O}", CooldownUntil: until);

        var (ok, reason) = AnswerLedger.From(history).Check(answer);
        if (!ok) return new(GateVerdict.Duplicate, reason);

        if (history.Count(s => PartStates.Rejected.Contains(s.Outcome)) >= maxSubmissions)
            return new(GateVerdict.LimitReached, $"submission limit of {maxSubmissions} reached for this part");

        return new(GateVerdict.Allowed);
    }

    public async Task<int> CountRejectedAsync(int year, int day, int part, CancellationToken ct = default) =>
        (await ledger.ListSubmissionsAsync(year, day, part, ct)).Count(s => PartStates.Rejected.Contains(s.Outcome));

    public async Task<AnswerLedger> LedgerForAsync(int year, int day, int part, CancellationToken ct = default) =>
        AnswerLedger.From(await ledger.ListSubmissionsAsync(year, day, part, ct));

    /// <summary>Writes the PENDING marker. Must complete before the answer is POSTed.</summary>
    public async Task<Submission> RecordPendingAsync(string runId, string? workflowRunId, int year, int day, int part, string answer,
        CancellationToken ct = default)
    {
        var submission = new Submission
        {
            Id = $"sub-{Guid.NewGuid():n}",
            Year = year,
            Day = day,
            Part = part,
            Answer = AnswerLedger.Normalize(answer),
            RunId = runId,
            WorkflowRunId = workflowRunId,
            Outcome = SubmissionOutcome.Pending,
            SubmittedAt = clock.UtcNow,
        };
        await ledger.UpsertSubmissionAsync(submission, ct);
        return submission;
    }

    /// <summary>Records the classified outcome; a wait from the website becomes an account-wide cooldown.</summary>
    public async Task<Submission> CompleteAsync(Submission pending, SubmissionOutcome outcome, string? message, int? waitSeconds,
        CancellationToken ct = default)
    {
        var done = pending with
        {
            Outcome = outcome,
            Message = message is { Length: > 300 } ? message[..300] : message,
            WaitSeconds = waitSeconds,
            RespondedAt = outcome == SubmissionOutcome.Uncertain ? null : clock.UtcNow,
        };
        await ledger.UpsertSubmissionAsync(done, ct);
        if (waitSeconds is > 0) await SetCooldownAsync(pending.Year, clock.UtcNow.AddSeconds(waitSeconds.Value + 1), ct);
        return done;
    }

    public async Task SetCooldownAsync(int year, DateTimeOffset until, CancellationToken ct = default)
    {
        var account = await ledger.GetAccountAsync(year, ct);
        if (account.CooldownUntil is { } existing && existing >= until) return;
        await ledger.UpsertAccountAsync(account with { CooldownUntil = until }, ct);
    }
}
