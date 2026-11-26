using Aps.Agent.Aoc;
using Aps.Agent.Domain;
using Aps.Agent.Persistence;
using Aps.Agent.Tests.Fakes;
using Aps.Agent.Verification;

namespace Aps.Agent.Tests.Verification;

/// <summary>
/// Submission integrity: a rejected answer is never submitted again, too-high/too-low answers set bounds, the
/// distinct-submission limit and cooldowns are enforced, and a pending marker is persisted before every POST.
/// </summary>
public sealed class AnswerLedgerTests
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    // ---- pure ledger

    [Fact]
    public void Rejected_answer_is_never_allowed_again()
    {
        var ledger = new AnswerLedger();
        ledger.Record("1234", SubmissionOutcome.Incorrect);

        var (ok, reason) = ledger.Check(" 1234 ");

        Assert.False(ok);
        Assert.Contains("already rejected", reason);
    }

    [Fact]
    public void Too_high_and_too_low_set_exclusive_bounds()
    {
        var ledger = new AnswerLedger();
        ledger.Record("500", SubmissionOutcome.TooHigh);
        ledger.Record("100", SubmissionOutcome.TooLow);
        ledger.Record("400", SubmissionOutcome.TooHigh); // tighter

        Assert.False(ledger.Check("400").Ok);
        Assert.False(ledger.Check("450").Ok);
        Assert.False(ledger.Check("100").Ok);
        Assert.False(ledger.Check("50").Ok);
        Assert.True(ledger.Check("399").Ok);
        Assert.True(ledger.Check("101").Ok);
        Assert.Contains("greater than 100", ledger.Feedback());
        Assert.Contains("less than 400", ledger.Feedback());
    }

    [Fact]
    public void Non_rejection_outcomes_do_not_change_the_ledger()
    {
        var ledger = new AnswerLedger();
        ledger.Record("7", SubmissionOutcome.Cooldown);
        ledger.Record("7", SubmissionOutcome.Uncertain);

        Assert.True(ledger.Check("7").Ok);
    }

    [Fact]
    public void Empty_answer_is_refused_and_non_numeric_answers_ignore_bounds()
    {
        var ledger = new AnswerLedger();
        ledger.Record("10", SubmissionOutcome.TooHigh);

        Assert.False(ledger.Check("  ").Ok);
        Assert.True(ledger.Check("ABC,DEF").Ok);
    }

    [Theory]
    [InlineData("return 12345", "12345", true)]
    [InlineData("x = 123456 + 1", "12345", false)]
    [InlineData("print(42)", "42", false)] // shorter than 4 characters is never flagged
    [InlineData("v = 3.14159", "14159", false)]
    public void Hardcode_detection_matches_whole_literals(string code, string answer, bool expected) =>
        Assert.Equal(expected, AnswerLedger.LooksHardcoded(code, answer));

    // ---- persisted gate (account-wide ledger in the store)

    private static async Task<(SubmissionGate Gate, InMemoryStore Store, LeaseClock Clock)> GateAsync()
    {
        var store = new InMemoryStore();
        var clock = new LeaseClock();
        return (new SubmissionGate(store.Ledger, clock), store, clock);
    }

    [Fact]
    public async Task Gate_refuses_a_previously_rejected_answer_from_any_run()
    {
        var (gate, store, clock) = await GateAsync();
        await store.Ledger.UpsertSubmissionAsync(new Submission
        {
            Id = "s1", Year = 2024, Day = 1, Part = 1, Answer = "99", Outcome = SubmissionOutcome.TooLow,
            RunId = "other-run", SubmittedAt = clock.UtcNow,
        }, Ct);

        var decision = await gate.EvaluateAsync(2024, 1, 1, "99", maxSubmissions: 3, Ct);

        Assert.Equal(GateVerdict.Duplicate, decision.Verdict);
        Assert.Equal(GateVerdict.Duplicate, (await gate.EvaluateAsync(2024, 1, 1, "42", 3, Ct)).Verdict); // below lower bound
    }

    [Fact]
    public async Task Gate_enforces_the_distinct_submission_limit()
    {
        var (gate, store, clock) = await GateAsync();
        foreach (var (id, answer) in new[] { ("a", "1"), ("b", "2"), ("c", "3") })
            await store.Ledger.UpsertSubmissionAsync(new Submission
            {
                Id = id, Year = 2024, Day = 2, Part = 1, Answer = answer, Outcome = SubmissionOutcome.Incorrect, SubmittedAt = clock.UtcNow,
            }, Ct);

        Assert.Equal(GateVerdict.LimitReached, (await gate.EvaluateAsync(2024, 2, 1, "4", maxSubmissions: 3, Ct)).Verdict);
    }

    [Fact]
    public async Task Gate_requires_reconciliation_while_a_submission_is_pending_or_uncertain()
    {
        foreach (var outcome in new[] { SubmissionOutcome.Pending, SubmissionOutcome.Uncertain })
        {
            var (gate, store, clock) = await GateAsync();
            await store.Ledger.UpsertSubmissionAsync(new Submission
            {
                Id = "p", Year = 2024, Day = 3, Part = 1, Answer = "5", Outcome = outcome, SubmittedAt = clock.UtcNow,
            }, Ct);

            var decision = await gate.EvaluateAsync(2024, 3, 1, "6", 3, Ct);

            Assert.Equal(GateVerdict.MustReconcile, decision.Verdict);
            Assert.Equal("p", decision.Unresolved!.Id);
        }
    }

    [Fact]
    public async Task Gate_waits_for_an_active_cooldown()
    {
        var (gate, store, clock) = await GateAsync();
        await store.Ledger.UpsertAccountAsync(new AccountState { Year = 2024, CooldownUntil = clock.UtcNow.AddSeconds(45) }, Ct);

        var decision = await gate.EvaluateAsync(2024, 4, 1, "7", 3, Ct);

        Assert.Equal(GateVerdict.Cooldown, decision.Verdict);
        Assert.Equal(clock.UtcNow.AddSeconds(45), decision.CooldownUntil);
        clock.Advance(TimeSpan.FromSeconds(46));
        Assert.Equal(GateVerdict.Allowed, (await gate.EvaluateAsync(2024, 4, 1, "7", 3, Ct)).Verdict);
    }

    [Fact]
    public async Task Pending_marker_is_persisted_before_the_post_and_completed_after()
    {
        var (gate, store, _) = await GateAsync();

        var pending = await gate.RecordPendingAsync("run-1", "wf-1", 2024, 5, 1, "123", Ct);
        var stored = Assert.Single(await store.Ledger.ListSubmissionsAsync(2024, 5, 1, Ct));
        Assert.Equal(SubmissionOutcome.Pending, stored.Outcome); // visible before any POST happens

        await gate.CompleteAsync(pending, SubmissionOutcome.TooHigh, "too high", waitSeconds: 60, Ct);
        var completed = Assert.Single(await store.Ledger.ListSubmissionsAsync(2024, 5, 1, Ct));
        Assert.Equal(SubmissionOutcome.TooHigh, completed.Outcome);
        Assert.NotNull(completed.RespondedAt);
        Assert.NotNull((await store.Ledger.GetAccountAsync(2024, Ct)).CooldownUntil); // wait recorded account-wide
    }

    [Fact]
    public async Task Distinct_rejections_count_towards_the_limit_but_cooldowns_do_not()
    {
        var (gate, store, clock) = await GateAsync();
        await store.Ledger.UpsertSubmissionAsync(new Submission
        {
            Id = "c", Year = 2024, Day = 6, Part = 1, Answer = "8", Outcome = SubmissionOutcome.Cooldown, SubmittedAt = clock.UtcNow,
        }, Ct);

        Assert.Equal(0, await gate.CountRejectedAsync(2024, 6, 1, Ct));
        Assert.Equal(GateVerdict.Allowed, (await gate.EvaluateAsync(2024, 6, 1, "8", 1, Ct)).Verdict);
    }
}
