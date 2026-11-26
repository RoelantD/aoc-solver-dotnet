using System.Text.Json;
using AGUI.Abstractions;
using AGUI.Server;
using Aps.Agent.Domain;
using Microsoft.Extensions.AI;

namespace Aps.Agent.Protocol;

/// <summary>A persisted domain event (or a heartbeat when <see cref="Event"/> is null) carried through the agent stream.</summary>
public sealed class DomainEventContent(DomainEvent? evt) : AIContent
{
    public DomainEvent? Event { get; } = evt;
}

/// <summary>A full dashboard state snapshot.</summary>
public sealed class StateSnapshotContent(JsonElement snapshot) : AIContent
{
    public JsonElement Snapshot { get; } = snapshot;
}

/// <summary>
/// Maps the agent's custom content to AG-UI events: a domain event becomes a CUSTOM event (plus a STATE_DELTA when it
/// carries a patch), a snapshot becomes STATE_SNAPSHOT. Both content types must also be registered for polymorphic
/// JSON, otherwise the AG-UI server fails the run with an opaque RUN_ERROR.
/// </summary>
public static class AguiMapping
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static void RegisterContentTypes(JsonSerializerOptions options)
    {
        AIJsonUtilities.AddAIContentType(options, typeof(DomainEventContent), "aps.domainEvent");
        AIJsonUtilities.AddAIContentType(options, typeof(StateSnapshotContent), "aps.stateSnapshot");
    }

    public static AGUIStreamOptions CreateStreamOptions() => new AGUIStreamOptions().MapContent(Map!);

    /// <summary>Returns the AG-UI events for one content item, or null for content this mapping does not own.</summary>
    public static IEnumerable<BaseEvent>? Map(AIContent content) => content switch
    {
        StateSnapshotContent s => [new StateSnapshotEvent { Snapshot = s.Snapshot }],
        DomainEventContent { Event: null } => [Heartbeat()],
        DomainEventContent { Event: { } e } => ToEvents(e),
        _ => null,
    };

    public static IEnumerable<BaseEvent> ToEvents(DomainEvent e)
    {
        yield return new CustomEvent { Name = e.Type, Value = JsonSerializer.SerializeToElement(Payload(e), Json) };
        if (e.Patch is { ValueKind: JsonValueKind.Array } patch) yield return new StateDeltaEvent { Delta = patch };
    }

    public static object Payload(DomainEvent e) => new
    {
        seq = e.Seq,
        ts = e.Ts,
        level = e.Level,
        day = e.Day,
        part = e.Part,
        agent = e.Agent,
        message = e.Message,
        data = e.Data,
        replay = e.Replay ? true : (bool?)null,
    };

    private static CustomEvent Heartbeat() => new()
    {
        Name = "heartbeat",
        Value = JsonSerializer.SerializeToElement(new { seq = 0, ts = DateTimeOffset.UtcNow }, Json),
    };
}
