using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aps.Agent.Abstractions;
using Aps.Agent.Agents;
using Aps.Agent.Aoc;
using Aps.Agent.Domain;
using Aps.Agent.Execution;
using Aps.Agent.Persistence;
using Aps.Agent.Security;
using Azure.Core;

namespace Aps.Agent.Preflight;

/// <summary>One preflight result, sent to the dashboard as a <c>preflight.check</c> event.</summary>
public sealed record PreflightCheck(string Check, bool Ok, string Detail, long Ms, string? MissingPermission = null, bool Skipped = false);

/// <summary>What to check: the year and the day range the operator is about to run.</summary>
public sealed record PreflightRequest(int Year, int DayFrom = 1, int DayTo = 25);

/// <summary>
/// Readiness checks under the identity the agent actually runs as, so a missing role assignment shows up before a run
/// rather than halfway through it. Never submits an answer and never requests a locked part: the website check reads
/// the calendar only. No secret or token value is ever reported, only identity claims.
/// </summary>
public sealed partial class PreflightService(
    IAgentTeam team, ICodeExecutor sandbox, IApsStore store, ISecretProvider secrets, AocClient aoc, TokenCredential credential)
{
    public static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(120);

    internal const string SandboxSolve = "def solve(input_text):\n    return str(sum(int(x) for x in input_text.split()))\n";

    internal const string EgressProbe = """
        import socket
        results = []
        for host, port in (("1.1.1.1", 443), ("adventofcode.com", 443)):
            try:
                s = socket.create_connection((host, port), timeout=4)
                s.close()
                results.append(f"{host}:allowed")
            except Exception as e:
                results.append(f"{host}:blocked({type(e).__name__})")
        print("EGRESS:" + ",".join(results))
        """;

    public static readonly string[] AllChecks = ["identity", "store", "keyvault", "website", "workhorse", "escalation", "sandbox"];

    public async IAsyncEnumerable<PreflightCheck> RunAsync(PreflightRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        yield return await Run("identity", IdentityAsync, ct);
        yield return await Run("store", StoreAsync, ct);
        var keyVault = await Run("keyvault", SecretAsync, ct);
        yield return keyVault;
        yield return keyVault.Ok
            ? await Run("website", c => WebsiteAsync(request, c), ct)
            : new PreflightCheck("website", false, "skipped: the session cookie could not be read", 0, Skipped: true);
        yield return await Run("workhorse", c => ModelAsync(AgentIds.Coder, c), ct);
        yield return team.EscalationAvailable
            ? await Run("escalation", c => ModelAsync(AgentIds.EscalationCoder, c), ct)
            : new PreflightCheck("escalation", false, "not configured", 0, Skipped: true);
        yield return await Run("sandbox", SandboxAsync, ct);
    }

    private static async Task<PreflightCheck> Run(string name, Func<CancellationToken, Task<string>> check, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(CheckTimeout);
        try
        {
            var detail = await check(timeout.Token);
            return new PreflightCheck(name, true, Redactor.RedactText(detail), sw.ElapsedMilliseconds);
        }
        catch (PreflightFailure f)
        {
            return new PreflightCheck(name, false, Redactor.RedactText(f.Message), sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new PreflightCheck(name, false, $"no answer within {CheckTimeout.TotalSeconds:F0}s", sw.ElapsedMilliseconds);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            var message = Redactor.RedactText(Short($"{e.GetType().Name}: {e.Message}", 400));
            return new PreflightCheck(name, false, message, sw.ElapsedMilliseconds, MissingPermissionIn(e));
        }
    }

    /// <summary>Who the agent is, so a failing check below can be fixed with a role assignment.</summary>
    private async Task<string> IdentityAsync(CancellationToken ct)
    {
        var token = await credential.GetTokenAsync(new TokenRequestContext(["https://ai.azure.com/.default"]), ct);
        var c = Claims(token.Token);
        return $"oid {c.Oid ?? "?"}, app {c.AppId ?? "?"}, type {c.Type ?? "user"}, tenant {c.Tenant ?? "?"}";
    }

    private async Task<string> StoreAsync(CancellationToken ct)
    {
        var runs = await store.Runs.ListAsync(ct);
        var kind = store is CosmosStore ? "Cosmos DB" : "in-memory store (not durable)";
        return $"{kind}: read {runs.Count} run(s)";
    }

    private async Task<string> SecretAsync(CancellationToken ct)
    {
        var value = await secrets.GetAocSessionAsync(ct);
        Redactor.RegisterSecret(value);
        var source = secrets is KeyVaultSecretProvider ? $"Key Vault secret '{KeyVaultSecretProvider.AocSessionSecretName}'" : "AOC_SESSION (local)";
        return value.Length > 0 ? $"{source} readable" : throw new PreflightFailure($"{source} is empty");
    }

    /// <summary>The session is logged in, and which parts in the range are still open. Reads the calendar only.</summary>
    private async Task<string> WebsiteAsync(PreflightRequest request, CancellationToken ct)
    {
        var status = await aoc.ValidateSessionAsync(request.Year, ct);
        if (!status.LoggedIn) throw new PreflightFailure(status.Detail);
        var stars = await aoc.CalendarAsync(request.Year, cancellationToken: ct);
        var days = Enumerable.Range(request.DayFrom, request.DayTo - request.DayFrom + 1).ToList();
        var open = days.Where(d => stars.GetValueOrDefault(d) < 2).ToList();
        var total = stars.Values.Sum();
        var range = request.DayFrom == request.DayTo ? $"day {request.DayFrom}" : $"days {request.DayFrom}–{request.DayTo}";
        var summary = open.Count == 0
            ? $"every part of {range} is already solved"
            : $"{open.Count} day(s) in {range} with open parts: {string.Join(", ", open.Take(10))}{(open.Count > 10 ? "…" : "")}";
        var contact = aoc.UserAgent.Contains("contact: unset", StringComparison.Ordinal) ? " (warning: AOC_CONTACT is not set)" : "";
        return $"logged in; {total} stars in {request.Year}; {summary}{contact}";
    }

    private async Task<string> ModelAsync(string agentId, CancellationToken ct)
    {
        var model = team.ModelFor(agentId);
        var agent = team.Create(agentId, "You are a connectivity check. Answer with one word.");
        var response = await agent.RunAsync("Say OK.", cancellationToken: ct);
        var usage = response.Usage is { } u ? $"; tokens in/out {u.InputTokenCount}/{u.OutputTokenCount}" : "";
        return $"{model.DisplayName} ({model.Deployment}) replied '{Short(response.Text.Trim(), 40)}'{usage}";
    }

    /// <summary>The sandbox runs code correctly and cannot reach the internet, so generated code cannot exfiltrate anything.</summary>
    private async Task<string> SandboxAsync(CancellationToken ct)
    {
        var sessionId = sandbox.NewSessionId();
        try
        {
            var run = await sandbox.RunCasesAsync(SandboxSolve, ["1 2 3\n"], 30, sessionId, ct);
            if (run.Status != ExecutionStatus.Ok || run.Results.FirstOrDefault()?.Output != "6")
                throw new PreflightFailure($"unexpected result: status={run.Status} error={Short(run.LoadError ?? run.Stderr, 200)}");
            var probe = await sandbox.RunRawAsync(EgressProbe, 30, sessionId, ct);
            var line = probe.Stdout.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("EGRESS:", StringComparison.Ordinal));
            if (line is null) throw new PreflightFailure($"egress probe produced no output: {Short(probe.Stderr, 200)}");
            if (line.Contains("allowed", StringComparison.Ordinal))
                throw new PreflightFailure($"code runs, but outbound network is ALLOWED ({line[7..]}); set the pool to EgressDisabled");
            return $"solve() ran correctly; egress blocked ({line[7..]})";
        }
        finally
        {
            await sandbox.ReleaseAsync(sessionId, CancellationToken.None);
        }
    }

    /// <summary>
    /// The data or control action a 401/403 names, so the operator knows which role is missing. Azure services word it
    /// differently (Cosmos "required RBAC permissions to perform action [...]", Key Vault "Action: '...'", Dynamic
    /// Sessions and Foundry "data action ..."), but all name a <c>Microsoft.*</c> action.
    /// </summary>
    public static string? MissingPermissionIn(Exception e)
    {
        var text = e.ToString();
        var status = e is Azure.RequestFailedException rfe ? rfe.Status : 0;
        var denied = status is 401 or 403 || DeniedPattern().IsMatch(text);
        return denied && ActionPattern().Match(text) is { Success: true } m ? m.Value.TrimEnd('.', ',', ']', '\'', '"') : null;
    }

    [GeneratedRegex(@"\b(401|403|forbidden|unauthori[sz]ed|not authori[sz]ed|does not have (required )?(rbac )?permissions?|lacks|AuthorizationFailed|PermissionDenied)\b", RegexOptions.IgnoreCase)]
    private static partial Regex DeniedPattern();

    [GeneratedRegex(@"Microsoft\.[A-Za-z]+(?:/[A-Za-z0-9._*-]+)+")]
    private static partial Regex ActionPattern();

    private sealed record TokenClaims(string? Oid, string? AppId, string? Type, string? Tenant);

    private static TokenClaims Claims(string jwt)
    {
        var payload = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
        string? Get(string c) => doc.RootElement.TryGetProperty(c, out var v) ? v.ToString() : null;
        return new(Get("oid"), Get("appid") ?? Get("azp"), Get("idtyp"), Get("tid"));
    }

    private static string Short(string? s, int max) => s is null ? "" : s.Length <= max ? s : s[..max] + "…";
}

/// <summary>A check ran but its result is wrong.</summary>
public sealed class PreflightFailure(string message) : Exception(message);
