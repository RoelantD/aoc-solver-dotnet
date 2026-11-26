using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Aps.Agent.Abstractions;
using Azure.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aps.Agent.Execution;

/// <summary>
/// Runs code in Azure Container Apps Dynamic Sessions: a Hyper-V isolated Python sandbox per session
/// identifier, in a pool with egress disabled. Only the generated program, its test/input text and the
/// harness are sent. No credentials, cookies or host files ever reach the sandbox: the bearer token
/// travels in the HTTP header only.
/// </summary>
/// <remarks>
/// Give the <see cref="HttpClient"/> an infinite (or generous) <see cref="HttpClient.Timeout"/>;
/// this class applies its own per-request timeout of <c>timeoutSeconds + 60</c>.
/// </remarks>
public sealed class DynamicSessionsExecutor(
    HttpClient http,
    TokenCredential credential,
    string poolEndpoint,
    IClock? clock = null,
    ILogger<DynamicSessionsExecutor>? logger = null,
    string apiVersion = DynamicSessionsExecutor.DefaultApiVersion) : ICodeExecutor
{
    public const string DefaultApiVersion = "2025-10-02-preview";
    public const string SessionManagementApiVersion = "2025-02-02-preview";
    public const string TokenScope = "https://dynamicsessions.io/.default";
    public const int StoreLimit = 8192;
    public const int MaxInfraTries = 5;

    private static readonly TimeSpan TokenRefreshMargin = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ReleaseTimeout = TimeSpan.FromSeconds(20);

    private readonly string _pool = poolEndpoint.TrimEnd('/');
    private readonly IClock _clock = clock ?? SystemClock.Instance;
    private readonly ILogger _log = logger ?? NullLogger<DynamicSessionsExecutor>.Instance;
    private AccessToken? _token;

    public string Name => "azure-dynamic-sessions";

    public string NewSessionId() => NewId();

    /// <summary>The session identifier format: <c>aps-{32 hex}</c>.</summary>
    public static string NewId() => $"aps-{Guid.NewGuid():N}";

    public async Task<ExecutionOutcome> RunCasesAsync(string code, IReadOnlyList<string> cases, int timeoutSeconds,
        string sessionId, CancellationToken cancellationToken = default)
    {
        var raw = await RunRawAsync(Harness.Build(code, cases), timeoutSeconds, sessionId, cancellationToken);
        var decoded = Harness.Decode(raw.Stdout);
        if (decoded is null)
        {
            var stderr = raw.Status == ExecutionStatus.Ok && raw.Stderr.Length == 0
                ? "harness produced no result (process crashed or was killed, e.g. memory limit)"
                : raw.Stderr;
            return new ExecutionOutcome
            {
                Status = raw.TimedOut ? ExecutionStatus.Timeout : ExecutionStatus.Error,
                Stdout = Tail(raw.Stdout),
                Stderr = Tail(stderr),
                DurationMs = raw.DurationMs,
                TimedOut = raw.TimedOut,
                CorrelationId = sessionId,
            };
        }

        return new ExecutionOutcome
        {
            Status = decoded.LoadError is { Length: > 0 } ? ExecutionStatus.Error : ExecutionStatus.Ok,
            Results = decoded.Results,
            LoadError = decoded.LoadError,
            Stdout = Tail(decoded.ProgramStdout is { Length: > 0 } p ? p : Harness.StripMarker(raw.Stdout)),
            Stderr = Tail(raw.Stderr),
            DurationMs = raw.DurationMs,
            TimedOut = false,
            CorrelationId = sessionId,
        };
    }

    public async Task<ExecutionOutcome> RunRawAsync(string code, int timeoutSeconds, string sessionId,
        CancellationToken cancellationToken = default)
    {
        var url = $"{_pool}/executions?api-version={apiVersion}&identifier={Uri.EscapeDataString(sessionId)}";
        var body = new
        {
            codeInputType = "Inline",
            executionType = "Synchronous",
            code,
            timeoutInSeconds = timeoutSeconds,
        };
        var started = _clock.UtcNow;
        using var response = await PostWithRetryAsync(url, body, TimeSpan.FromSeconds(timeoutSeconds + 60), cancellationToken);
        var duration = (int)(_clock.UtcNow - started).TotalMilliseconds;
        var text = await response.Content.ReadAsStringAsync(cancellationToken);

        var status = (int)response.StatusCode;
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new SandboxInfraException($"sandbox authorization failed (HTTP {status}); " +
                "check the 'Azure ContainerApps Session Executor' role", status);
        if (status >= 400)
            throw new SandboxInfraException($"sandbox rejected request: HTTP {status} {Truncate(text, 300)}", status);

        return ParseExecution(text, sessionId, duration);
    }

    public async Task ReleaseAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var url = $"{_pool}/session?api-version={SessionManagementApiVersion}&identifier={Uri.EscapeDataString(sessionId)}";
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ReleaseTimeout);
            using var request = new HttpRequestMessage(HttpMethod.Delete, url);
            request.Headers.Authorization = await AuthorizationAsync(timeout.Token);
            using var _ = await http.SendAsync(request, timeout.Token);
        }
        catch (Exception ex) // cleanup must never break solving
        {
            _log.LogInformation("sandbox.release_failed {Error}", ex.GetType().Name);
        }
    }

    internal static ExecutionOutcome ParseExecution(string json, string sessionId, int measuredMs)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new SandboxInfraException("sandbox returned a response that is not JSON", inner: ex);
        }

        using var _ = document;
        var root = document.RootElement;
        var props = root.TryGetProperty("properties", out var p) && p.ValueKind == JsonValueKind.Object ? p : root;
        var result = props.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.Object ? r : props;

        var status = (Text(props, "status") ?? "").ToLowerInvariant();
        var stdout = Text(result, "stdout") ?? "";
        var stderr = Text(result, "stderr") ?? "";
        var errText = Truncate(Text(props, "error") is { Length: > 0 } e ? e : Text(root, "error") ?? "", 2000);

        var timedOut = errText.Contains("timeout", StringComparison.OrdinalIgnoreCase)
                       || (stderr + errText).Contains("timed out", StringComparison.OrdinalIgnoreCase);
        var failed = status is "failed" or "canceled";
        var outcome = !failed ? ExecutionStatus.Ok : timedOut ? ExecutionStatus.Timeout : ExecutionStatus.Error;

        var execMs = result.TryGetProperty("executionTimeInMilliseconds", out var ms) && ms.ValueKind == JsonValueKind.Number
            ? (int)ms.GetDouble()
            : measuredMs;

        return new ExecutionOutcome
        {
            Status = outcome,
            Stdout = stdout,
            Stderr = (stderr + (errText.Length > 0 ? "\n" + errText : "")).Trim(),
            DurationMs = execMs,
            TimedOut = outcome == ExecutionStatus.Timeout,
            CorrelationId = sessionId,
        };
    }

    /// <summary>
    /// POST with infra retries (408/409/429/5xx/network/request timeout). Other statuses are returned.
    /// Unlike an answer submission, re-running code in a sandbox has no side effects, so retrying is safe.
    /// </summary>
    private async Task<HttpResponseMessage> PostWithRetryAsync(string url, object body, TimeSpan requestTimeout,
        CancellationToken cancellationToken)
    {
        var backoff = TimeSpan.FromSeconds(2);
        string lastError = "";
        int? lastStatus = null;
        TimeSpan wait = backoff;
        for (var attempt = 1; attempt <= MaxInfraTries; attempt++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(requestTimeout);
            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
            request.Headers.Authorization = await AuthorizationAsync(cancellationToken);
            try
            {
                var response = await http.SendAsync(request, timeout.Token);
                var code = (int)response.StatusCode;
                if (code is not (408 or 409 or 429) && code < 500)
                    return response;
                lastStatus = code;
                lastError = $"HTTP {code}";
                wait = RetryAfter(response) ?? backoff;
                response.Dispose();
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException
                                       && !cancellationToken.IsCancellationRequested)
            {
                lastStatus = null;
                lastError = $"network error: {ex.GetType().Name}";
                wait = backoff;
            }

            if (attempt == MaxInfraTries)
                break;
            wait = wait > MaxBackoff ? MaxBackoff : wait;
            await _clock.DelayAsync(wait, cancellationToken);
            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
        }

        throw new SandboxInfraException($"sandbox service unavailable after {MaxInfraTries} tries ({lastError})",
            lastStatus, wait > MaxBackoff ? MaxBackoff : wait);
    }

    private async ValueTask<AuthenticationHeaderValue> AuthorizationAsync(CancellationToken cancellationToken)
    {
        if (_token is not { } token || token.ExpiresOn - TokenRefreshMargin <= _clock.UtcNow)
        {
            token = await credential.GetTokenAsync(new TokenRequestContext([TokenScope]), cancellationToken);
            _token = token;
        }

        return new AuthenticationHeaderValue("Bearer", token.Token);
    }

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        foreach (var name in new[] { "retry-after-ms", "x-ms-retry-after-ms" })
        {
            if (response.Headers.TryGetValues(name, out var values)
                && double.TryParse(values.First(), NumberStyles.Float, CultureInfo.InvariantCulture, out var ms))
                return TimeSpan.FromMilliseconds(ms);
        }

        if (response.Headers.TryGetValues("retry-after", out var ra)
            && double.TryParse(ra.First(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
            return TimeSpan.FromSeconds(seconds);
        return null;
    }

    /// <summary>String value of a non-null property (non-string values as raw JSON), else null.</summary>
    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                JsonValueKind.String => v.GetString(),
                _ => v.GetRawText(),
            }
            : null;

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    private static string Tail(string s) => s.Length <= StoreLimit ? s : s[^StoreLimit..];
}
