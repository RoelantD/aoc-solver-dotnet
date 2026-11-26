// Sample 01: one agent, one tool.
// A Microsoft Agent Framework (MAF) agent on GPT-6.1 Sol (Microsoft Foundry) that may call `run_python`, a C# function
// exposed as a tool. The tool executes code in an Azure Container Apps Dynamic Sessions sandbox (no outbound network).
// Authentication is Entra ID throughout (DefaultAzureCredential), no API keys.
//
// Needs FOUNDRY_PROJECT_ENDPOINT and SESSIONS_POOL_ENDPOINT, set by `. .\scripts\env.ps1` in this shell
// (or from .env via a VS Code launch configuration):
//   . .\scripts\env.ps1
//   dotnet run --project samples/01-single-agent-tool
using System.ComponentModel;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.AI.Projects;
using Azure.Core;
using Azure.Identity;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

static string Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v
    : throw new InvalidOperationException($"{name} is not set. Run '. .\\scripts\\env.ps1' in this terminal first.");

var credential = new DefaultAzureCredential();
var pool = Env("SESSIONS_POOL_ENDPOINT");
using var http = new HttpClient();

// The [Description] attributes become the tool's schema: the model reads them to decide when and how to call it.
[Description("Runs Python 3 code in an isolated sandbox without network access and returns what it printed.")]
async Task<string> RunPython([Description("A complete Python program that prints its result.")] string code)
{
    Console.WriteLine($"\n🔧 run_python ({code.Split('\n').Length} lines)");
    var token = await credential.GetTokenAsync(new TokenRequestContext(["https://dynamicsessions.io/.default"]), default);
    using var request = new HttpRequestMessage(HttpMethod.Post,
        $"{pool}/executions?api-version=2025-10-02-preview&identifier=sample-{Guid.NewGuid():n}")
    {
        Content = JsonContent.Create(new { codeInputType = "Inline", executionType = "Synchronous", code, timeoutInSeconds = 30 }),
    };
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
    using var response = await http.SendAsync(request);
    var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("result");
    var output = result.GetProperty("stdout").GetString() + result.GetProperty("stderr").GetString();
    Console.WriteLine($"   → {output.Trim()}");
    return output;
}

// AsAIAgent wraps a Foundry model deployment as a MAF AIAgent; AIFunctionFactory turns the local function into a tool.
// The agent runs the tool-calling loop itself: model → run_python → model, until it has a final answer.
AIAgent agent = new AIProjectClient(new Uri(Env("FOUNDRY_PROJECT_ENDPOINT")), credential)
    .AsAIAgent(
        model: Environment.GetEnvironmentVariable("WORKHORSE_DEPLOYMENT") ?? "gpt-6.1-sol",
        name: "puzzle-solver",
        instructions: "You solve small puzzles. Always verify your answer by running Python with the run_python tool. " +
                      "Reply with the final answer and one sentence on how you got it.",
        tools: [AIFunctionFactory.Create(RunPython, name: "run_python")]);

const string puzzle = """
    Each line of the ledger lists two lantern readings. The drift of a line is the absolute difference between them.
    Ledger:
    3 5
    10 4
    7 7
    What is the total drift?
    """;

// RunAsync returns the complete response. RunStreamingAsync is not used: with the pinned OpenAI 2.15.0 +
// Microsoft.Extensions.AI.OpenAI 10.11.0 the Responses streaming path throws MissingMethodException.
// The tool calls above still print live.
var response = await agent.RunAsync(puzzle);
Console.WriteLine($"\n💬 {response.Text}");
