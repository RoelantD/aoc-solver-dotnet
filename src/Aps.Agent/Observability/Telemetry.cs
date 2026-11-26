using System.Diagnostics;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Aps.Agent.Observability;

/// <summary>
/// OpenTelemetry for agents, model calls, workflow and sandbox. Azure Monitor (wired by the
/// agent host) when the platform injects <c>APPLICATIONINSIGHTS_CONNECTION_STRING</c>; OTLP (the Aspire dashboard) when set.
/// Sensitive data capture is disabled on every agent; the AG-UI server's real error causes are logged.
/// </summary>
public static class Telemetry
{
    public static readonly ActivitySource Sandbox = new("Aps.Sandbox");
    public static readonly ActivitySource Workflow = new("Aps.Workflow");

    private static readonly string[] Sources =
    [
        "Aps.Agents", "Aps.Workflow", "Aps.Sandbox", "Experimental.Microsoft.Agents.AI", "Experimental.Microsoft.Agents.AI.*",
        "Microsoft.Agents.AI*", "Experimental.Microsoft.Extensions.AI", "Experimental.AGUI.Server", "Azure.AI.AgentServer*",
    ];

    public static void Configure(IServiceCollection services, IConfiguration config)
    {
        // Azure Monitor: the official agent host (Azure.AI.AgentServer.Core) already calls the cross-cutting
        // UseAzureMonitorExporter when APPLICATIONINSIGHTS_CONNECTION_STRING is set; adding the signal-specific exporters
        // here as well throws NotSupportedException at startup.
        // The host's exporter picks up the sources and meters registered on the same OpenTelemetry builder.
        var otlp = config["OTEL_EXPORTER_OTLP_ENDPOINT"];
        services.AddOpenTelemetry()
            .WithTracing(t =>
            {
                t.AddSource(Sources);
                // Before the exporters, so no secret in a tag leaves the process.
                t.AddProcessor(new RedactingProcessor());
                if (!string.IsNullOrEmpty(otlp)) t.AddOtlpExporter();
            })
            .WithMetrics(m =>
            {
                m.AddMeter("Experimental.Microsoft.Agents.AI*", "Microsoft.Agents.AI*", "Experimental.Microsoft.Extensions.AI");
                if (!string.IsNullOrEmpty(otlp)) m.AddOtlpExporter();
            });

        // The AG-UI server reports only a generic RUN_ERROR; log the real cause.
        ActivitySource.AddActivityListener(new ActivityListener
        {
            ShouldListenTo = s => s.Name.StartsWith("Experimental.AGUI", StringComparison.Ordinal),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = a =>
            {
                if (a.Status == ActivityStatusCode.Error) Console.Error.WriteLine($"[agui] {a.DisplayName} error: {a.StatusDescription}");
            },
        });
    }

    /// <summary>Starts a workflow-step span for one part, tagged with the part coordinates.</summary>
    public static Activity? StartStep(string name, int year, int day, int part, int? revision = null)
    {
        var a = Workflow.StartActivity(name);
        a?.SetTag("aps.year", year);
        a?.SetTag("aps.day", day);
        a?.SetTag("aps.part", part);
        if (revision is { } r) a?.SetTag("aps.revision", r);
        return a;
    }

    /// <summary>Sets the span's outcome tag and status.</summary>
    public static void SetOutcome(this Activity? a, string outcome, bool ok)
    {
        if (a is null) return;
        a.SetTag("aps.outcome", outcome);
        a.SetStatus(ok ? ActivityStatusCode.Ok : ActivityStatusCode.Error, ok ? null : outcome);
    }
}

/// <summary>
/// Applies the <see cref="Security.Redactor"/> to every string tag and the status description of a finished span.
/// Instrumentation can put request details (headers, URLs, exception text) in tags; registered secrets such as the
/// session cookie are scrubbed here before any exporter sees the span.
/// </summary>
public sealed class RedactingProcessor : OpenTelemetry.BaseProcessor<Activity>
{
    public override void OnEnd(Activity data)
    {
        foreach (var tag in data.TagObjects.ToList())
            if (tag.Value is string s && Security.Redactor.RedactText(s) is var r && r != s)
                data.SetTag(tag.Key, r);
        if (data.StatusDescription is { } d && Security.Redactor.RedactText(d) is var rd && rd != d)
            data.SetStatus(data.Status, rd);
    }
}
