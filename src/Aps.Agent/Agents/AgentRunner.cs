using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aps.Agent.Domain;
using Microsoft.Agents.AI;

namespace Aps.Agent.Agents;

/// <summary>A model is unavailable (429, 5xx, network, timeout). Pauses the run; never consumes an attempt.</summary>
public sealed class ModelInfraException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The model answered, but the answer could not be used (invalid JSON, missing fields).</summary>
public sealed class ModelOutputException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Where model-call events and usage go (the part workflow supplies this).</summary>
public interface IAgentCallSink
{
    Task OnStartedAsync(string agentId, ModelInfo model, string purpose, CancellationToken ct);

    Task OnFinishedAsync(string agentId, ModelInfo model, string purpose, UsageTotals usage, string? error, CancellationToken ct);
}

/// <summary>
/// Calls an agent for a structured result: <c>RunAsync&lt;T&gt;</c> first, then a fenced-JSON fallback for
/// endpoints that reject structured output. Records usage per agent and classifies failures, because an outage
/// (pause, retry later) must be handled differently from a bad answer (try again with feedback). Reasoning content
/// is never read or stored.
/// </summary>
public sealed partial class AgentRunner(IAgentTeam team)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };
    private readonly HashSet<string> _noStructuredOutput = [];

    public async Task<T> RunAsync<T>(string agentId, string instructions, string user, string purpose, IAgentCallSink sink,
        CancellationToken ct)
    {
        var model = team.ModelFor(agentId);
        var agent = team.Create(agentId, instructions);
        await sink.OnStartedAsync(agentId, model, purpose, ct);
        var sw = Stopwatch.StartNew();
        AgentResponse? response = null;
        try
        {
            T result;
            if (!_noStructuredOutput.Contains(model.Key))
            {
                try
                {
                    var typed = await agent.RunAsync<T>(user, cancellationToken: ct);
                    response = typed;
                    result = typed.Result;
                }
                catch (Exception e) when (IsStructuredOutputUnsupported(e))
                {
                    _noStructuredOutput.Add(model.Key); // skip the failing attempt on later calls
                    (result, response) = await RunFencedAsync<T>(agent, user, ct);
                }
            }
            else
            {
                (result, response) = await RunFencedAsync<T>(agent, user, ct);
            }
            if (result is null) throw new ModelOutputException("the model returned an empty result");
            await sink.OnFinishedAsync(agentId, model, purpose, Usage(response, sw, model), null, ct);
            return result;
        }
        catch (Exception e) when (Classify(e) is { } classified)
        {
            await sink.OnFinishedAsync(agentId, model, purpose, Usage(response, sw, model), classified.Message, ct);
            throw classified;
        }
    }

    private static async Task<(T, AgentResponse)> RunFencedAsync<T>(AIAgent agent, string user, CancellationToken ct)
    {
        var response = await agent.RunAsync(user + "\n\nReturn ONLY the JSON object, inside a ```json fenced block.", cancellationToken: ct);
        var text = response.Text ?? "";
        var match = FencedJson().Match(text);
        var json = match.Success ? match.Groups[1].Value : text.Trim();
        try
        {
            return (JsonSerializer.Deserialize<T>(json, Json) ?? throw new ModelOutputException("empty JSON"), response);
        }
        catch (JsonException e)
        {
            throw new ModelOutputException($"response is not valid JSON: {e.Message}", e);
        }
    }

    private static UsageTotals Usage(AgentResponse? response, Stopwatch sw, ModelInfo model)
    {
        var u = response?.Usage;
        long input = u?.InputTokenCount ?? 0, output = u?.OutputTokenCount ?? 0;
        return new UsageTotals
        {
            Calls = 1,
            InputTokens = input,
            OutputTokens = output,
            ReasoningTokens = u?.ReasoningTokenCount ?? 0,
            LatencyMs = sw.ElapsedMilliseconds,
            CostUsd = model.Cost(input, output),
        };
    }

    /// <summary>Maps an exception to infra vs output failure; null for exceptions that must propagate as-is.</summary>
    internal static Exception? Classify(Exception e)
    {
        switch (e)
        {
            case ModelInfraException or ModelOutputException:
                return e;
            case OperationCanceledException:
                return null; // operator pause/stop or shutdown
            case JsonException:
                return new ModelOutputException($"response is not valid JSON: {e.Message}", e);
            case HttpRequestException or TimeoutException or IOException:
                return new ModelInfraException($"model unreachable: {e.Message}", e);
        }
        var status = StatusOf(e);
        if (status is 408 or 429 || status >= 500) return new ModelInfraException($"model unavailable (HTTP {status})", e);
        if (status is 401 or 403) return new ModelInfraException($"model access denied (HTTP {status}); check role assignments", e);
        if (status is 400) return new ModelOutputException($"model rejected the request (HTTP 400): {First(e.Message)}", e);
        return e.InnerException is { } inner ? Classify(inner) : null;
    }

    private static bool IsStructuredOutputUnsupported(Exception e) =>
        StatusOf(e) == 400 && Regex.IsMatch(e.Message, "format|schema|output_config|response_format", RegexOptions.IgnoreCase)
        || e is NotSupportedException;

    /// <summary>Reads an HTTP status from SDK exceptions (ClientResultException.Status, Anthropic *.StatusCode, ...).</summary>
    private static int? StatusOf(Exception e)
    {
        if (e is HttpRequestException { StatusCode: { } code }) return (int)code;
        foreach (var name in new[] { "Status", "StatusCode" })
        {
            var value = e.GetType().GetProperty(name)?.GetValue(e);
            switch (value)
            {
                case int i when i > 0: return i;
                case HttpStatusCode h: return (int)h;
            }
        }
        return null;
    }

    private static string First(string s) => s.Split('\n')[0] is { Length: > 200 } l ? l[..200] : s.Split('\n')[0];

    [GeneratedRegex(@"```(?:json)?\s*(\{[\s\S]*\})\s*```")]
    private static partial Regex FencedJson();
}
