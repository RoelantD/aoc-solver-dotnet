using System.Text;
using Aps.Agent.Abstractions;
using Aps.Agent.Execution;
using Aps.Agent.Preflight;
using Aps.Agent.Tests.Fakes;
using Aps.Agent.Tests.Workflow;
using Azure.Core;

namespace Aps.Agent.Tests.Preflight;

public sealed class PreflightServiceTests
{
    private const string Blocked = "EGRESS:1.1.1.1:blocked(TimeoutError),adventofcode.com:blocked(gaierror)";

    private static async Task<List<PreflightCheck>> RunAsync(RunHarness h, ISecretProvider? secrets = null, PreflightRequest? request = null)
    {
        var service = new PreflightService(h.Team, h.Sandbox, h.Store, secrets ?? new FakeSecretProvider("test-session-cookie"), h.Aoc,
            new FakeCredential());
        var results = new List<PreflightCheck>();
        await foreach (var c in service.RunAsync(request ?? new PreflightRequest(2024, 1, 2), TestContext.Current.CancellationToken)) results.Add(c);
        return results;
    }

    private static RunHarness Healthy(string egress = Blocked)
    {
        var h = new RunHarness();
        h.Site.Completed[1] = [1, 2];
        h.Team.Reply(Domain.AgentIds.Coder, new ScriptedReply("OK")).Reply(Domain.AgentIds.EscalationCoder, new ScriptedReply("OK"));
        h.Sandbox.Enqueue(FakeSandbox.Ok("6")).Enqueue(new ExecutionOutcome { Status = ExecutionStatus.Ok, Stdout = egress + "\n" });
        return h;
    }

    [Fact]
    public async Task All_checks_pass_and_nothing_is_submitted()
    {
        using var h = Healthy();
        var results = await RunAsync(h);

        Assert.Equal(PreflightService.AllChecks, results.Select(r => r.Check));
        Assert.All(results, r => Assert.True(r.Ok, $"{r.Check}: {r.Detail}"));
        Assert.Contains("oid 00000000-aaaa", results.Single(r => r.Check == "identity").Detail);
        Assert.Contains("1 day(s) in days 1–2 with open parts: 2", results.Single(r => r.Check == "website").Detail);
        Assert.Contains("egress blocked", results.Single(r => r.Check == "sandbox").Detail);
        Assert.Empty(h.Site.Posts);
        Assert.DoesNotContain(h.Site.Requests, r => r.Method != "GET" || r.Path.Contains("/day/", StringComparison.Ordinal));
        Assert.DoesNotContain(results, r => r.Detail.Contains("test-session-cookie", StringComparison.Ordinal));
        Assert.Single(h.Sandbox.Released);
    }

    [Fact]
    public async Task Open_egress_fails_the_sandbox_check()
    {
        using var h = Healthy("EGRESS:1.1.1.1:allowed,adventofcode.com:allowed");
        var sandbox = (await RunAsync(h)).Single(r => r.Check == "sandbox");
        Assert.False(sandbox.Ok);
        Assert.Contains("ALLOWED", sandbox.Detail);
    }

    [Fact]
    public async Task Logged_out_session_fails_the_website_check()
    {
        using var h = Healthy();
        h.Site.LoggedIn = false;
        var website = (await RunAsync(h)).Single(r => r.Check == "website");
        Assert.False(website.Ok);
        Assert.Contains("not logged in", website.Detail);
    }

    [Fact]
    public async Task Unreadable_secret_fails_key_vault_and_skips_the_website()
    {
        using var h = Healthy();
        var results = await RunAsync(h, new ThrowingSecrets());
        var vault = results.Single(r => r.Check == "keyvault");
        Assert.False(vault.Ok);
        Assert.Equal("Microsoft.KeyVault/vaults/secrets/getSecret/action", vault.MissingPermission);
        Assert.True(results.Single(r => r.Check == "website").Skipped);
        Assert.Empty(h.Site.Requests);
    }

    [Theory]
    [InlineData("Request blocked by Auth cosmos-aps : Request is blocked because principal [abc] does not have required RBAC permissions to perform action [Microsoft.DocumentDB/databaseAccounts/readMetadata] on any scope.",
        "Microsoft.DocumentDB/databaseAccounts/readMetadata")]
    [InlineData("HTTP 403: The principal lacks the required data action Microsoft.App/sessionPools/executions/write.",
        "Microsoft.App/sessionPools/executions/write")]
    [InlineData("Model replied with garbage about Microsoft.Foo/bar", null)]
    public void Missing_permission_is_parsed_from_denials_only(string message, string? expected) =>
        Assert.Equal(expected, PreflightService.MissingPermissionIn(new InvalidOperationException(message)));

    private sealed class ThrowingSecrets : ISecretProvider
    {
        public ValueTask<string> GetAocSessionAsync(CancellationToken cancellationToken = default) =>
            throw new Azure.RequestFailedException(403, "Caller is not authorized to perform action on resource. Action: 'Microsoft.KeyVault/vaults/secrets/getSecret/action'");
    }

    /// <summary>An unsigned token with the claims the identity check reports.</summary>
    private sealed class FakeCredential : TokenCredential
    {
        private static readonly string Jwt = "e30." + Convert.ToBase64String(Encoding.UTF8.GetBytes(
            """{"oid":"00000000-aaaa-bbbb-cccc-000000000001","appid":"app-1","idtyp":"app","tid":"tenant-1"}""")).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".sig";

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new(Jwt, DateTimeOffset.UtcNow.AddHours(1));

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }
}
