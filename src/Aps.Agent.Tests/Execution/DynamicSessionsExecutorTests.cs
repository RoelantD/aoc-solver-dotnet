using System.Net;
using System.Text;
using System.Text.Json;
using Aps.Agent.Execution;

namespace Aps.Agent.Tests.Execution;

public class DynamicSessionsExecutorTests
{
    private const string Pool = "https://pool.example.test/";
    private const string Token = "eyJ-SECRET-TOKEN-value-1234";
    private const string Session = "aps-0123456789abcdef0123456789abcdef";
    private static readonly DateTimeOffset Now = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeHttpHandler _handler = new();
    private readonly FakeTokenCredential _credential = new(Token, Now.AddHours(1));
    private readonly FakeClock _clock = new(Now);

    private DynamicSessionsExecutor Executor() =>
        new(new HttpClient(_handler) { Timeout = Timeout.InfiniteTimeSpan }, _credential, Pool, _clock);

    private static string Execution(string status, string stdout = "", string stderr = "", string? error = null, int? ms = 42) =>
        JsonSerializer.Serialize(new
        {
            properties = new
            {
                status,
                error,
                result = new { stdout, stderr, executionTimeInMilliseconds = ms },
            },
        });

    private static string HarnessStdout(params object[] results) =>
        "debug\n" + HarnessTests.Marker(new { load_error = (string?)null, results, stdout = "printed" }) + "\n";

    [Fact]
    public async Task Posts_unwrapped_body_to_executions_with_bearer_token()
    {
        _handler.Respond(HttpStatusCode.OK, Execution("Succeeded", stdout: "42\n"));

        var outcome = await Executor().RunRawAsync("print(6*7)", 30, Session, TestContext.Current.CancellationToken);

        var request = Assert.Single(_handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal($"https://pool.example.test/executions?api-version=2025-10-02-preview&identifier={Session}", request.Uri.ToString());
        Assert.Equal($"Bearer {Token}", request.Authorization);
        Assert.Equal(["https://dynamicsessions.io/.default"], Assert.Single(_credential.Scopes));

        using var body = JsonDocument.Parse(request.Body);
        var root = body.RootElement;
        Assert.False(root.TryGetProperty("properties", out _));
        Assert.Equal(["codeInputType", "executionType", "code", "timeoutInSeconds"], root.EnumerateObject().Select(p => p.Name));
        Assert.Equal("Inline", root.GetProperty("codeInputType").GetString());
        Assert.Equal("Synchronous", root.GetProperty("executionType").GetString());
        Assert.Equal("print(6*7)", root.GetProperty("code").GetString());
        Assert.Equal(30, root.GetProperty("timeoutInSeconds").GetInt32());

        Assert.Equal(ExecutionStatus.Ok, outcome.Status);
        Assert.Equal("42\n", outcome.Stdout);
        Assert.Equal(42, outcome.DurationMs);
        Assert.Equal(Session, outcome.CorrelationId);
    }

    [Fact]
    public async Task Sandbox_code_never_contains_the_token_or_other_secrets()
    {
        _handler.Respond(HttpStatusCode.OK, Execution("Succeeded", stdout: HarnessStdout()));
        const string program = "def solve(t):\n    return len(t)\n";
        string[] cases = ["1 2 3\n"];

        await Executor().RunCasesAsync(program, cases, 30, Session, TestContext.Current.CancellationToken);

        var code = JsonDocument.Parse(Assert.Single(_handler.Requests).Body).RootElement.GetProperty("code").GetString()!;
        Assert.Equal(Harness.Build(program, cases), code); // exactly the harness, nothing added
        var decodedPayloads = string.Concat(
            System.Text.RegularExpressions.Regex.Matches(code, @"b64decode\(""([A-Za-z0-9+/=]*)""\)")
                .Select(m => Encoding.UTF8.GetString(Convert.FromBase64String(m.Groups[1].Value))));
        foreach (var secret in new[] { Token, "SECRET", "Bearer", "dynamicsessions.io" })
        {
            Assert.DoesNotContain(secret, code, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(secret, decodedPayloads, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task RunCases_decodes_the_marker()
    {
        _handler.Respond(HttpStatusCode.OK, Execution("Succeeded",
            stdout: HarnessStdout(new { index = 0, output = "8", error = (string?)null, duration_ms = 3 }), ms: 120));

        var outcome = await Executor().RunCasesAsync("code", ["in"], 30, Session, TestContext.Current.CancellationToken);

        Assert.Equal(ExecutionStatus.Ok, outcome.Status);
        Assert.Equal(new CaseResult(0, "8", null, 3), Assert.Single(outcome.Results));
        Assert.Equal("printed", outcome.Stdout);
        Assert.Equal(120, outcome.DurationMs);
        Assert.False(outcome.TimedOut);
    }

    [Fact]
    public async Task RunCases_with_load_error_is_error()
    {
        var stdout = HarnessTests.Marker(new { load_error = "NameError: x", results = Array.Empty<object>(), stdout = "" });
        _handler.Respond(HttpStatusCode.OK, Execution("Succeeded", stdout: "before\n" + stdout));

        var outcome = await Executor().RunCasesAsync("code", ["in"], 30, Session, TestContext.Current.CancellationToken);

        Assert.Equal(ExecutionStatus.Error, outcome.Status);
        Assert.Equal("NameError: x", outcome.LoadError);
        Assert.Equal("before", outcome.Stdout); // program stdout empty -> raw stdout without marker
    }

    [Fact]
    public async Task Crash_without_marker_is_error_with_explanation()
    {
        _handler.Respond(HttpStatusCode.OK, Execution("Succeeded", stdout: "partial output"));

        var outcome = await Executor().RunCasesAsync("code", ["in"], 30, Session, TestContext.Current.CancellationToken);

        Assert.Equal(ExecutionStatus.Error, outcome.Status);
        Assert.False(outcome.TimedOut);
        Assert.Empty(outcome.Results);
        Assert.Equal("partial output", outcome.Stdout);
        Assert.Contains("harness produced no result", outcome.Stderr);
        Assert.Contains("memory limit", outcome.Stderr);
    }

    [Fact]
    public async Task Failed_execution_without_marker_keeps_platform_stderr()
    {
        _handler.Respond(HttpStatusCode.OK, Execution("Failed", stderr: "Killed", error: "OOMKilled"));

        var outcome = await Executor().RunCasesAsync("code", ["in"], 30, Session, TestContext.Current.CancellationToken);

        Assert.Equal(ExecutionStatus.Error, outcome.Status);
        Assert.Equal("Killed\nOOMKilled", outcome.Stderr);
    }

    [Theory]
    [InlineData("Failed", "Execution timeout exceeded", "")]
    [InlineData("Canceled", null, "process timed out")]
    public async Task Platform_timeout_maps_to_timeout(string status, string? error, string stderr)
    {
        _handler.Respond(HttpStatusCode.OK, Execution(status, stderr: stderr, error: error));

        var outcome = await Executor().RunCasesAsync("code", ["in"], 30, Session, TestContext.Current.CancellationToken);

        Assert.Equal(ExecutionStatus.Timeout, outcome.Status);
        Assert.True(outcome.TimedOut);
    }

    [Fact]
    public async Task Parses_flat_response_and_long_output_is_tail_truncated()
    {
        var longOut = new string('x', 10_000) + "END";
        _handler.Respond(HttpStatusCode.OK, JsonSerializer.Serialize(new { status = "Succeeded", stdout = longOut, stderr = "" }));

        var outcome = await Executor().RunCasesAsync("code", ["in"], 30, Session, TestContext.Current.CancellationToken);

        Assert.Equal(DynamicSessionsExecutor.StoreLimit, outcome.Stdout.Length);
        Assert.EndsWith("END", outcome.Stdout);
    }

    [Fact]
    public async Task Retries_429_honouring_retry_after_then_succeeds()
    {
        _handler
            .Respond(HttpStatusCode.TooManyRequests, configure: r => r.Headers.Add("retry-after", "7"))
            .Respond(HttpStatusCode.OK, Execution("Succeeded", stdout: "ok"));

        var outcome = await Executor().RunRawAsync("x", 30, Session, TestContext.Current.CancellationToken);

        Assert.Equal("ok", outcome.Stdout);
        Assert.Equal([TimeSpan.FromSeconds(7)], _clock.Delays);
        Assert.Equal(2, _handler.Requests.Count);
        Assert.Equal(1, _credential.Calls); // token cached across retries
    }

    [Fact]
    public async Task Persistent_5xx_raises_infra_exception_with_retry_hint()
    {
        for (var i = 0; i < DynamicSessionsExecutor.MaxInfraTries; i++)
            _handler.Respond(HttpStatusCode.ServiceUnavailable, configure: r => r.Headers.Add("retry-after-ms", "1500"));

        var ex = await Assert.ThrowsAsync<SandboxInfraException>(() =>
            Executor().RunRawAsync("x", 30, Session, TestContext.Current.CancellationToken));

        Assert.Equal(503, ex.StatusCode);
        Assert.Equal(TimeSpan.FromMilliseconds(1500), ex.RetryAfter);
        Assert.Contains("after 5 tries (HTTP 503)", ex.Message);
        Assert.Equal(DynamicSessionsExecutor.MaxInfraTries, _handler.Requests.Count);
        Assert.Equal(4, _clock.Delays.Count);
    }

    [Fact]
    public async Task Network_errors_back_off_exponentially_then_raise()
    {
        for (var i = 0; i < DynamicSessionsExecutor.MaxInfraTries; i++)
            _handler.Throw(new HttpRequestException("connection refused"));

        var ex = await Assert.ThrowsAsync<SandboxInfraException>(() =>
            Executor().RunRawAsync("x", 30, Session, TestContext.Current.CancellationToken));

        Assert.Null(ex.StatusCode);
        Assert.Contains("network error: HttpRequestException", ex.Message);
        Assert.Equal([2, 4, 8, 16], _clock.Delays.Select(d => d.TotalSeconds));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Auth_failures_raise_infra_exception_without_retry(HttpStatusCode status)
    {
        _handler.Respond(status);

        var ex = await Assert.ThrowsAsync<SandboxInfraException>(() =>
            Executor().RunRawAsync("x", 30, Session, TestContext.Current.CancellationToken));

        Assert.Contains("Azure ContainerApps Session Executor", ex.Message);
        Assert.DoesNotContain(Token, ex.Message);
        Assert.Single(_handler.Requests);
    }

    [Fact]
    public async Task Bad_request_raises_infra_exception_without_retry()
    {
        _handler.Respond(HttpStatusCode.BadRequest, "{\"error\":\"bad body\"}");

        var ex = await Assert.ThrowsAsync<SandboxInfraException>(() =>
            Executor().RunRawAsync("x", 30, Session, TestContext.Current.CancellationToken));

        Assert.Equal(400, ex.StatusCode);
        Assert.StartsWith("sandbox rejected request: HTTP 400", ex.Message);
        Assert.Single(_handler.Requests);
    }

    [Fact]
    public async Task Token_is_refreshed_only_near_expiry()
    {
        var credential = new FakeTokenCredential(Token, Now.AddMinutes(10));
        var executor = new DynamicSessionsExecutor(new HttpClient(_handler), credential, Pool, _clock);
        _handler.Respond(HttpStatusCode.OK, Execution("Succeeded")).Respond(HttpStatusCode.OK, Execution("Succeeded"))
            .Respond(HttpStatusCode.OK, Execution("Succeeded"));

        await executor.RunRawAsync("x", 30, Session, TestContext.Current.CancellationToken);
        await executor.RunRawAsync("x", 30, Session, TestContext.Current.CancellationToken);
        Assert.Equal(1, credential.Calls);

        _clock.UtcNow = Now.AddMinutes(6); // within the 5-minute refresh margin
        await executor.RunRawAsync("x", 30, Session, TestContext.Current.CancellationToken);
        Assert.Equal(2, credential.Calls);
    }

    [Fact]
    public async Task Release_deletes_the_session_and_never_throws()
    {
        _handler.Respond(HttpStatusCode.NoContent).Throw(new HttpRequestException("down"));
        var executor = Executor();

        await executor.ReleaseAsync(Session, TestContext.Current.CancellationToken);
        await executor.ReleaseAsync(Session, TestContext.Current.CancellationToken); // swallowed

        var request = _handler.Requests[0];
        Assert.Equal(HttpMethod.Delete, request.Method);
        Assert.Equal($"https://pool.example.test/session?api-version=2025-02-02-preview&identifier={Session}", request.Uri.ToString());
        Assert.Equal($"Bearer {Token}", request.Authorization);
    }

    [Fact]
    public void Session_ids_are_aps_guid_and_unique()
    {
        var executor = Executor();
        var a = executor.NewSessionId();
        Assert.Matches("^aps-[0-9a-f]{32}$", a);
        Assert.NotEqual(a, executor.NewSessionId());
    }
}
