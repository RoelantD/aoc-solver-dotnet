// Agentic Advent of Code Solver — Microsoft Agent Framework on a Microsoft Foundry hosted agent.
//
// The official agent host (Azure.AI.AgentServer.Core) provides what Foundry expects: port 8088, the /readiness
// probe, request ids and tracing. The dashboard protocol, AG-UI, is registered as the hosted-agent Invocations
// protocol.
using Aps.Agent.Abstractions;
using Aps.Agent.Agents;
using Aps.Agent.Aoc;
using Aps.Agent.Execution;
using Aps.Agent.Observability;
using Aps.Agent.Persistence;
using Aps.Agent.Protocol;
using Aps.Agent.Security;
using Azure.AI.AgentServer.Core;
using Azure.Core;
using Azure.Identity;
using Microsoft.Agents.AI.Hosting.AGUI.AspNetCore;

// Event messages and stored documents must not depend on the host machine's culture (decimal separators, dates).
System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = System.Globalization.CultureInfo.InvariantCulture;

var host = AgentHost.CreateBuilder(args);
var config = host.Configuration;
string? Env(string name) => config[name] is { Length: > 0 } v ? v : null;

host.Services.AddAGUIServer();
host.Services.ConfigureHttpJsonOptions(o => AguiMapping.RegisterContentTypes(o.SerializerOptions));
host.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins((Env("APS_CORS_ORIGINS") ?? "http://localhost:5175,http://127.0.0.1:5175").Split(','))
    .AllowAnyHeader().AllowAnyMethod().WithExposedHeaders("x-agent-session-id")));

// ---- identity: the hosted agent's Entra identity in Azure, the developer's az login locally (no keys anywhere)
TokenCredential credential = new DefaultAzureCredential();
host.Services.AddSingleton(credential);
host.Services.AddSingleton<IClock>(SystemClock.Instance);

// ---- durable state; in-memory only for local development without Cosmos
host.Services.AddSingleton<IApsStore>(sp =>
{
    if (Env("COSMOS_ENDPOINT") is { } cosmos)
    {
        var store = new CosmosStore(cosmos, credential);
        store.InitializeAsync().GetAwaiter().GetResult();
        return store;
    }
    sp.GetRequiredService<ILogger<Program>>().LogWarning("COSMOS_ENDPOINT not set: using the in-memory store (not durable)");
    return new InMemoryStore();
});
host.Services.AddSingleton(sp => new EventLog(sp.GetRequiredService<IApsStore>().Events));

// ---- secrets: the Advent of Code session cookie
host.Services.AddSingleton<ISecretProvider>(_ => Env("KEYVAULT_URI") is { } vault
    ? new KeyVaultSecretProvider(new Uri(vault), credential)
    : new DevelopmentSecretProvider(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".env")));

// ---- agents, sandbox, website
host.Services.AddSingleton<IAgentTeam>(_ => new FoundryAgentTeam(ModelSettings.FromEnvironment(Env), credential));
host.Services.AddSingleton<ICodeExecutor>(sp => new DynamicSessionsExecutor(
    new HttpClient { Timeout = Timeout.InfiniteTimeSpan }, credential,
    Env("SESSIONS_POOL_ENDPOINT") ?? throw new InvalidOperationException("SESSIONS_POOL_ENDPOINT is not set"),
    sp.GetRequiredService<IClock>(), sp.GetRequiredService<ILogger<DynamicSessionsExecutor>>()));
host.Services.AddSingleton(sp => new AocClient(
    sp.GetRequiredService<ISecretProvider>(), sp.GetRequiredService<IApsStore>().Cache, sp.GetRequiredService<IClock>(),
    new AocClientOptions { Contact = Env("AOC_CONTACT") ?? "", RepoUrl = Env("APS_REPO_URL") ?? "" },
    logger: sp.GetRequiredService<ILogger<AocClient>>()));
host.Services.AddSingleton<RunManager>();
host.Services.AddSingleton<Aps.Agent.Preflight.PreflightService>();

// ---- observability
Telemetry.Configure(host.Services, config);

var streamOptions = AguiMapping.CreateStreamOptions();
host.RegisterProtocol("Invocations", endpoints => endpoints
    .MapAGUIServer("/invocations", http => new RunStreamAgent(
        http.RequestServices.GetRequiredService<IApsStore>(),
        http.RequestServices.GetRequiredService<EventLog>(),
        http.RequestServices.GetRequiredService<RunManager>(),
        http.RequestServices.GetRequiredService<IClock>(),
        RunStreamAgent.OperatorFrom(http),
        http.RequestServices.GetRequiredService<Aps.Agent.Preflight.PreflightService>()))
    .WithMetadata(streamOptions));

var app = host.Build();
app.App.UseCors();
app.Run();
