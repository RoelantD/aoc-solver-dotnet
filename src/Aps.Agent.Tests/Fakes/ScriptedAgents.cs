using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Aps.Agent.Agents;
using Aps.Agent.Domain;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Aps.Agent.Tests.Fakes;

/// <summary>One scripted model reply: JSON text (or an exception) plus token usage.</summary>
public sealed record ScriptedReply(object? Json = null, Exception? Error = null, int InputTokens = 100, int OutputTokens = 50);

/// <summary>
/// An <see cref="IChatClient"/> that replays scripted replies. Wrapped in a real <see cref="ChatClientAgent"/>, so the
/// Agent Framework structured-output path is exercised without calling a model.
/// </summary>
public sealed class ScriptedChatClient(string name, Func<IReadOnlyList<ChatMessage>, ScriptedReply> next) : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = messages.ToList();
        var reply = next(list);
        if (reply.Error is not null) throw reply.Error;
        var text = reply.Json is string s ? s : JsonSerializer.Serialize(reply.Json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text))
        {
            ModelId = name,
            Usage = new UsageDetails { InputTokenCount = reply.InputTokens, OutputTokenCount = reply.OutputTokens },
        });
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var update in (await GetResponseAsync(messages, options, cancellationToken)).ToChatResponseUpdates()) yield return update;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}

/// <summary>
/// The solving team with scripted models. Each role has its own reply queue; an empty queue fails the test loudly.
/// </summary>
public sealed class ScriptedTeam : IAgentTeam
{
    private readonly ConcurrentDictionary<string, ConcurrentQueue<ScriptedReply>> _queues = new();
    public ConcurrentQueue<(string Agent, string Prompt)> Prompts { get; } = new();
    public bool EscalationAvailable { get; set; } = true;

    public ScriptedTeam Reply(string agentId, params ScriptedReply[] replies)
    {
        var q = _queues.GetOrAdd(agentId, _ => new());
        foreach (var r in replies) q.Enqueue(r);
        return this;
    }

    /// <summary>Handover requests go to the Coder's model; queue them under this key.</summary>
    public const string Handover = "coder:handover";

    public ModelInfo ModelFor(string agentId) => agentId == AgentIds.EscalationCoder
        ? new ModelInfo("escalation", "claude-test", "Claude (fake)", 15, 75)
        : new ModelInfo("workhorse", "gpt-test", "GPT (fake)", 1, 4);

    public AIAgent Create(string agentId, string instructions)
    {
        var key = agentId == AgentIds.Coder && instructions == Agents.Prompts.HandoverInstructions ? Handover : agentId;
        var client = new ScriptedChatClient(key, messages =>
        {
            Prompts.Enqueue((key, messages.LastOrDefault()?.Text ?? ""));
            return _queues.TryGetValue(key, out var q) && q.TryDequeue(out var r)
                ? r
                : throw new InvalidOperationException($"no scripted reply left for {key}");
        });
        return new ChatClientAgent(client, instructions: instructions, name: agentId);
    }

    public static ScriptedReply Analysis(string summary, params (string Input, string? Expected)[] examples) => new(new
    {
        summary, algorithm = "Parse each line and aggregate.", complexity = "O(n)",
        examples = examples.Select(e => new { input = e.Input, expected_output = e.Expected, note = "" }), parameter_notes = "",
    });

    public static ScriptedReply Program(string code, string algorithm = "Sum per line") =>
        new(new { diagnosis = "", algorithm, code });

    public static ScriptedReply HandoverReply() => new(new
    {
        what_was_tried = "Two attempts with string parsing", failing_cases = "Example 1 returned 0",
        suspected_cause = "Lines were not split", key_constraints = "Sum absolute differences",
    });

    public static ScriptedReply RateLimited() =>
        new(Error: new HttpRequestException("rate limited", null, HttpStatusCode.TooManyRequests));
}
