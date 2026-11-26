using Aps.Agent.Domain;

namespace Aps.Agent.Workflow;

/// <summary>Which agent makes attempt N, and whether this attempt is the handoff.</summary>
public sealed record AttemptPlan(string Agent, string Kind, string? HandoffReason = null);

/// <summary>
/// Deterministic escalation policy. The model never decides when to hand off to the stronger (more expensive) model;
/// plain code does, which keeps attempts and spend bounded and predictable.
/// </summary>
public static class EscalationPolicy
{
    public static AttemptPlan Plan(int number, RunConfig cfg, string? lastFailure, bool alreadyEscalated,
        bool escalationAvailable, bool afterRejection = false)
    {
        var failures = number - 1;
        var kind = number == 1 ? "initial" : afterRejection ? "post_rejection" : "repair";
        if (escalationAvailable)
        {
            if (alreadyEscalated) return new(AgentIds.EscalationCoder, kind);
            string? reason = null;
            if (cfg.EscalateAfterFailures > 0 && failures >= cfg.EscalateAfterFailures)
                reason = $"{failures} unsuccessful attempts by the Coder";
            else if (cfg.EscalateOnTimeout && lastFailure == "timeout" && number >= 2)
                reason = "the previous solution timed out (algorithmic complexity)";
            if (reason is not null) return new(AgentIds.EscalationCoder, "escalation", reason);
        }
        return new(AgentIds.Coder, kind);
    }
}
