using Anthropic;
using Anthropic.Foundry;
using Aps.Agent.Domain;
using Azure.AI.Projects;
using Azure.Core;
using Microsoft.Agents.AI;

namespace Aps.Agent.Agents;

/// <summary>A model behind an agent: key, deployment and display name (for events and usage).</summary>
public sealed record ModelInfo(string Key, string Deployment, string DisplayName, double PriceInputPerMTok = 0, double PriceOutputPerMTok = 0)
{
    public double Cost(long input, long output) => (input * PriceInputPerMTok + output * PriceOutputPerMTok) / 1_000_000d;
}

/// <summary>The solving team. Instructions are passed per call because they depend on the run's limits.</summary>
public interface IAgentTeam
{
    ModelInfo ModelFor(string agentId);

    bool EscalationAvailable { get; }

    /// <summary>Creates the agent for a role with the given instructions.</summary>
    AIAgent Create(string agentId, string instructions);
}

/// <summary>Model configuration read from the environment (azure.yaml env / local settings).</summary>
public sealed record ModelSettings
{
    public required string ProjectEndpoint { get; init; }
    public required string FoundryResourceName { get; init; }
    public string WorkhorseDeployment { get; init; } = "gpt-6.1-sol";
    public string WorkhorseDisplayName { get; init; } = "GPT-6.1 Sol";
    public string EscalationDeployment { get; init; } = "claude-opus-5-5";
    public string EscalationDisplayName { get; init; } = "Claude Opus 5.5";
    public double WorkhorsePriceIn { get; init; }
    public double WorkhorsePriceOut { get; init; }
    public double EscalationPriceIn { get; init; }
    public double EscalationPriceOut { get; init; }

    public static ModelSettings FromEnvironment(Func<string, string?> env) => new()
    {
        ProjectEndpoint = env("FOUNDRY_PROJECT_ENDPOINT") ?? throw new InvalidOperationException("FOUNDRY_PROJECT_ENDPOINT is not set"),
        FoundryResourceName = env("FOUNDRY_RESOURCE_NAME") ?? throw new InvalidOperationException("FOUNDRY_RESOURCE_NAME is not set"),
        WorkhorseDeployment = env("WORKHORSE_DEPLOYMENT") ?? "gpt-6.1-sol",
        WorkhorseDisplayName = env("WORKHORSE_DISPLAY_NAME") ?? "GPT-6.1 Sol",
        EscalationDeployment = env("ESCALATION_DEPLOYMENT") ?? "claude-opus-5-5",
        EscalationDisplayName = env("ESCALATION_DISPLAY_NAME") ?? "Claude Opus 5.5",
        WorkhorsePriceIn = Num(env("WORKHORSE_PRICE_INPUT_PER_MTOK")),
        WorkhorsePriceOut = Num(env("WORKHORSE_PRICE_OUTPUT_PER_MTOK")),
        EscalationPriceIn = Num(env("ESCALATION_PRICE_INPUT_PER_MTOK")),
        EscalationPriceOut = Num(env("ESCALATION_PRICE_OUTPUT_PER_MTOK")),
    };

    private static double Num(string? s) =>
        double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
}

/// <summary>
/// The production team on Microsoft Foundry: GPT-6.1 Sol through the project's Responses endpoint
/// for the Analyst and Coder, Claude Opus 5.5 through the Claude endpoint on Foundry for the Escalation Coder.
/// Both authenticate with the caller's Entra identity (the hosted agent identity in Azure).
/// </summary>
public sealed class FoundryAgentTeam(ModelSettings settings, TokenCredential credential) : IAgentTeam
{
    public const string TelemetrySource = "Aps.Agents";

    private readonly AIProjectClient _project = new(new Uri(settings.ProjectEndpoint), credential);
    private readonly AnthropicFoundryClient _claude = new(new AnthropicFoundryIdentityTokenCredentials(
        credential, settings.FoundryResourceName, ["https://ai.azure.com/.default"]));

    private readonly ModelInfo _workhorse = new("workhorse", settings.WorkhorseDeployment, settings.WorkhorseDisplayName,
        settings.WorkhorsePriceIn, settings.WorkhorsePriceOut);
    private readonly ModelInfo _escalation = new("escalation", settings.EscalationDeployment, settings.EscalationDisplayName,
        settings.EscalationPriceIn, settings.EscalationPriceOut);

    public bool EscalationAvailable => true;

    public ModelInfo ModelFor(string agentId) => agentId == AgentIds.EscalationCoder ? _escalation : _workhorse;

    public AIAgent Create(string agentId, string instructions)
    {
        AIAgent agent = agentId == AgentIds.EscalationCoder
            ? _claude.AsAIAgent(model: _escalation.Deployment, instructions: instructions, name: agentId,
                description: AgentIds.DisplayName(agentId))
            : _project.AsAIAgent(model: _workhorse.Deployment, instructions: instructions, name: agentId,
                description: AgentIds.DisplayName(agentId));
        return agent.AsBuilder()
            .UseOpenTelemetry(TelemetrySource, cfg => cfg.EnableSensitiveData = false)
            .Build();
    }
}
