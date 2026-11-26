using System.Globalization;
using System.Net;
using Aps.Agent.Abstractions;
using Aps.Agent.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aps.Agent.Aoc;

public class AocException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class AocAuthException(string message) : AocException(message);

public sealed class AocLockedException(string message) : AocException(message);

/// <summary>The POST never left the machine (the connection could not be established).</summary>
public sealed class AocNotSentException(string message, Exception? inner = null) : AocException(message, inner);

/// <summary>The POST may have reached the server, but no response was received.</summary>
public sealed class AocUncertainException(string message, Exception? inner = null) : AocException(message, inner);

public sealed record SessionStatus(bool LoggedIn, string Detail);

public sealed record AocClientOptions
{
    /// <summary>Maintainer contact for the User-Agent, so the site owner can reach whoever runs this.</summary>
    public string Contact { get; init; } = "";

    public string RepoUrl { get; init; } = "";

    public string ProductVersion { get; init; } = "1.0.0";

    /// <summary><c>AOC_MIN_INTERVAL_S</c>: minimum gap between consecutive requests.</summary>
    public TimeSpan MinInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>The minimum interval can never be set below this.</summary>
    public TimeSpan MinIntervalFloor { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// The only component allowed to contact adventofcode.com. Advent of Code is a free hobby site that
/// asks automated tools to go easy on it, so requests are sequential (one lock), paced (minimum
/// interval), identified (User-Agent with contact) and cached (inputs forever; pages and the calendar
/// until the caller asks for a refresh, which it does only after an accepted answer or a reconcile).
/// Redirects are never followed and POSTs are never retried.
/// </summary>
public sealed class AocClient : IDisposable
{
    public const int GetMaxTries = 5;
    public static readonly TimeSpan BackoffCap = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan InitialBackoff = TimeSpan.FromSeconds(2);

    private readonly ISecretProvider _secrets;
    private readonly IPuzzleCache _cache;
    private readonly IClock _clock;
    private readonly ILogger _logger;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly TimeSpan _floor;
    private DateTimeOffset? _lastRequest;
    private string? _session;

    /// <param name="handler">
    /// Transport; tests inject a fake. When null an <see cref="HttpClientHandler"/> is created. Automatic
    /// redirects are switched off on <see cref="HttpClientHandler"/> and <see cref="SocketsHttpHandler"/>.
    /// </param>
    public AocClient(
        ISecretProvider secrets,
        IPuzzleCache cache,
        IClock clock,
        AocClientOptions options,
        HttpMessageHandler? handler = null,
        ILogger<AocClient>? logger = null)
    {
        _secrets = secrets;
        _cache = cache;
        _clock = clock;
        _logger = logger ?? NullLogger<AocClient>.Instance;
        _floor = options.MinIntervalFloor;
        MinInterval = Max(options.MinInterval, _floor);

        handler ??= new HttpClientHandler();
        switch (handler)
        {
            case HttpClientHandler h:
                h.AllowAutoRedirect = false;
                h.UseCookies = false;
                break;
            case SocketsHttpHandler s:
                s.AllowAutoRedirect = false;
                s.UseCookies = false;
                break;
        }

        UserAgent = $"agentic-puzzle-solver/{options.ProductVersion} "
            + $"(+{(options.RepoUrl.Length > 0 ? options.RepoUrl : "local")}; "
            + $"contact: {(options.Contact.Length > 0 ? options.Contact : "unset")})";
        _http = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = new Uri(UrlAllowlist.AocOrigin),
            Timeout = options.RequestTimeout,
        };
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
    }

    public string UserAgent { get; }

    public TimeSpan MinInterval { get; private set; }

    /// <summary>Called after every page, input or calendar lookup with (kind, servedFromCache).</summary>
    public Func<string, bool, ValueTask>? OnFetch { get; set; }

    /// <summary>Sets the minimum interval, clamped to the floor (default 1 s).</summary>
    public void SetMinInterval(TimeSpan interval) => MinInterval = Max(interval, _floor);

    public async ValueTask<bool> HasSessionAsync(CancellationToken cancellationToken = default) =>
        (await SessionAsync(cancellationToken)).Length > 0;

    public async Task<SessionStatus> ValidateSessionAsync(int year, CancellationToken cancellationToken = default)
    {
        if (!await HasSessionAsync(cancellationToken))
        {
            return new(false, "AOC_SESSION is not configured");
        }

        var (status, body) = await GetAsync($"/{year}", cancellationToken);
        await NotifyAsync("calendar", cached: false);
        if (status != HttpStatusCode.OK)
        {
            return new(false, $"calendar returned HTTP {(int)status}");
        }

        if (!PageParser.IsLoggedIn(body))
        {
            return new(false, "session cookie is not logged in (expired or invalid)");
        }

        await _cache.SetAsync(year, 0, "calendar", body, cancellationToken);
        return new(true, "logged in");
    }

    /// <summary>Day to stars (0..2). The cached calendar (stored under day 0) is used unless <paramref name="refresh"/>.</summary>
    public async Task<IReadOnlyDictionary<int, int>> CalendarAsync(
        int year, bool refresh = false, CancellationToken cancellationToken = default)
    {
        if (!refresh && await _cache.GetAsync(year, 0, "calendar", cancellationToken) is { } cached)
        {
            await NotifyAsync("calendar", cached: true);
            return PageParser.ParseCalendarStars(cached);
        }

        var (status, body) = await GetAsync($"/{year}", cancellationToken);
        await NotifyAsync("calendar", cached: false);
        if (status != HttpStatusCode.OK)
        {
            throw new AocException($"calendar returned HTTP {(int)status}");
        }

        if (await HasSessionAsync(cancellationToken) && !PageParser.IsLoggedIn(body))
        {
            throw new AocAuthException("session is not logged in");
        }

        await _cache.SetAsync(year, 0, "calendar", body, cancellationToken);
        return PageParser.ParseCalendarStars(body);
    }

    /// <summary>
    /// The day page, from cache unless <paramref name="refresh"/>. Callers refresh only after an accepted
    /// answer (to reveal Part 2) or when reconciling against account state.
    /// </summary>
    public async Task<DayPage> DayPageAsync(int year, int day, bool refresh = false, CancellationToken cancellationToken = default)
    {
        if (!refresh && await _cache.GetAsync(year, day, "page", cancellationToken) is { } cached)
        {
            await NotifyAsync("page", cached: true);
            return PageParser.ParseDayPage(cached);
        }

        var (status, body) = await GetAsync($"/{year}/day/{day}", cancellationToken);
        await NotifyAsync("page", cached: false);
        if (status == HttpStatusCode.NotFound)
        {
            throw new AocLockedException($"day {day} is not available");
        }

        if (IsRedirect(status))
        {
            throw new AocAuthException("redirected while fetching puzzle page");
        }

        if (status != HttpStatusCode.OK)
        {
            throw new AocException($"puzzle page returned HTTP {(int)status}");
        }

        var page = PageParser.ParseDayPage(body);
        if (await HasSessionAsync(cancellationToken) && !page.LoggedIn)
        {
            throw new AocAuthException("session is not logged in");
        }

        await _cache.SetAsync(year, day, "page", body, cancellationToken);
        return page;
    }

    /// <summary>
    /// The personal puzzle input; cached permanently after the first fetch. It goes to the sandbox only, never
    /// into a model prompt: inputs are per-user and Advent of Code asks that they not be redistributed.
    /// </summary>
    public async Task<string> InputAsync(int year, int day, CancellationToken cancellationToken = default)
    {
        if (await _cache.GetAsync(year, day, "input", cancellationToken) is { } cached)
        {
            await NotifyAsync("input", cached: true);
            return cached;
        }

        var (status, body) = await GetAsync($"/{year}/day/{day}/input", cancellationToken);
        await NotifyAsync("input", cached: false);
        if (status == HttpStatusCode.BadRequest || IsRedirect(status))
        {
            throw new AocAuthException("input requires a valid session");
        }

        if (status == HttpStatusCode.NotFound)
        {
            throw new AocLockedException($"input for day {day} is not available");
        }

        if (status != HttpStatusCode.OK)
        {
            throw new AocException($"input returned HTTP {(int)status}");
        }

        await _cache.SetAsync(year, day, "input", body, cancellationToken);
        return body;
    }

    /// <summary>
    /// POSTs an answer exactly once. A retry could submit the same answer twice: a timed-out POST may
    /// still have been processed, and every wrong answer costs a growing cooldown. The caller decides
    /// what to do with an uncertain outcome (reconcile against the day page), never this method.
    /// </summary>
    /// <exception cref="AocNotSentException">The connection could not be established.</exception>
    /// <exception cref="AocUncertainException">The request may have been processed, but no response arrived.</exception>
    public async Task<SubmitResult> SubmitAsync(int year, int day, int level, string answer, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(level, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(level, 2);

        HttpResponseMessage response;
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var form = new FormUrlEncodedContent([new("level", level.ToString(CultureInfo.InvariantCulture)), new("answer", answer)]);
            try
            {
                response = await SendAsync(HttpMethod.Post, $"/{year}/day/{day}/answer", form, cancellationToken);
            }
            catch (HttpRequestException ex) when (IsNotSent(ex))
            {
                throw new AocNotSentException(ex.HttpRequestError.ToString(), ex);
            }
            catch (Exception ex) when (IsTransportFailure(ex, cancellationToken))
            {
                throw new AocUncertainException(ex.GetType().Name, ex);
            }
        }
        finally
        {
            _lock.Release();
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var wait = RetryAfter(response) is { } ra && ra > TimeSpan.Zero ? (int)ra.TotalSeconds : 60;
                return new(SubmissionOutcome.Cooldown, "HTTP 429 Too Many Requests", wait);
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            return ResponseClassifier.Classify(body, (int)response.StatusCode);
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _lock.Dispose();
    }

    /// <summary>GETs are idempotent, so transient failures (network, 429, 5xx) are retried with capped exponential backoff.</summary>
    private async Task<(HttpStatusCode Status, string Body)> GetAsync(string path, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var delay = InitialBackoff;
            for (var attempt = 1; ; attempt++)
            {
                HttpResponseMessage response;
                try
                {
                    response = await SendAsync(HttpMethod.Get, path, content: null, cancellationToken);
                }
                catch (Exception ex) when (IsTransportFailure(ex, cancellationToken))
                {
                    if (attempt == GetMaxTries)
                    {
                        throw new AocException($"network error fetching {path}: {ex.GetType().Name}", ex);
                    }

                    _logger.LogWarning("aoc.get.retry {Path} {Error} retry in {RetryIn}", path, ex.GetType().Name, delay);
                    await _clock.DelayAsync(delay, cancellationToken);
                    delay = Min(delay * 2, BackoffCap);
                    continue;
                }

                using (response)
                {
                    var status = response.StatusCode;
                    if (status == HttpStatusCode.TooManyRequests || (int)status >= 500)
                    {
                        if (attempt == GetMaxTries)
                        {
                            throw new AocException($"server error {(int)status} fetching {path}");
                        }

                        // A zero or missing Retry-After falls back to the backoff delay.
                        var wait = RetryAfter(response) is { } ra && ra > TimeSpan.Zero ? ra : delay;
                        _logger.LogWarning("aoc.get.retry {Path} {Status} retry in {RetryIn}", path, (int)status, wait);
                        await _clock.DelayAsync(wait, cancellationToken);
                        delay = Min(delay * 2, BackoffCap);
                        continue;
                    }

                    return (status, await response.Content.ReadAsStringAsync(cancellationToken));
                }
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Paces and sends one request. Caller holds the lock.</summary>
    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent? content, CancellationToken cancellationToken)
    {
        if (!path.StartsWith('/') || path.Contains("//", StringComparison.Ordinal) || path.Contains("..", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"invalid AoC path: {path}");
        }

        var uri = new Uri(_http.BaseAddress!, path);
        if (!UrlAllowlist.IsAllowedAocUrl(uri))
        {
            throw new InvalidOperationException("AoC request outside the allowlist");
        }

        var session = await SessionAsync(cancellationToken);
        await PaceAsync(cancellationToken);
        try
        {
            using var request = new HttpRequestMessage(method, uri) { Content = content };
            if (session.Length > 0)
            {
                request.Headers.TryAddWithoutValidation("Cookie", $"session={session}");
            }

            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
            if (IsRedirect(response.StatusCode))
            {
                LogRedirect(uri, response.Headers.Location?.OriginalString);
            }

            return response;
        }
        finally
        {
            _lastRequest = _clock.UtcNow;
        }
    }

    private void LogRedirect(Uri requestUri, string? location)
    {
        // Redirects are never followed; an off-allowlist target is reported (host only, never the full URL).
        try
        {
            UrlAllowlist.ValidateRedirect(requestUri, location);
            _logger.LogWarning("aoc.redirect.not_followed {Path}", requestUri.AbsolutePath);
        }
        catch (DisallowedUrlException ex)
        {
            _logger.LogWarning("aoc.redirect.disallowed {Path}: {Reason}", requestUri.AbsolutePath, Redactor.RedactText(ex.Message));
        }
    }

    /// <summary>Waits until at least <see cref="MinInterval"/> has passed since the previous request, whatever its kind.</summary>
    private async Task PaceAsync(CancellationToken cancellationToken)
    {
        if (_lastRequest is { } last)
        {
            var wait = last + MinInterval - _clock.UtcNow;
            if (wait > TimeSpan.Zero)
            {
                await _clock.DelayAsync(wait, cancellationToken);
            }
        }
    }

    private async ValueTask<string> SessionAsync(CancellationToken cancellationToken)
    {
        if (_session is null)
        {
            var value = await _secrets.GetAocSessionAsync(cancellationToken) ?? "";
            Redactor.RegisterSecret(value);
            _session = value;
        }

        return _session;
    }

    private async ValueTask NotifyAsync(string kind, bool cached)
    {
        if (OnFetch is { } onFetch)
        {
            await onFetch(kind, cached);
        }
    }

    private TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Retry-After", out var values) || values.FirstOrDefault() is not { } raw)
        {
            return null;
        }

        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && double.IsFinite(seconds))
        {
            return TimeSpan.FromSeconds(Math.Max(0, seconds));
        }

        return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
            ? Max(at - _clock.UtcNow, TimeSpan.Zero)
            : null;
    }

    private static bool IsRedirect(HttpStatusCode status) => (int)status is 301 or 302 or 303 or 307 or 308;

    /// <summary>Failures where the request provably never reached the server.</summary>
    private static bool IsNotSent(HttpRequestException ex) =>
        ex.HttpRequestError is HttpRequestError.NameResolutionError
            or HttpRequestError.ConnectionError
            or HttpRequestError.SecureConnectionError
            or HttpRequestError.ProxyTunnelError;

    /// <summary>Network errors and timeouts, but not cancellation requested by the caller.</summary>
    private static bool IsTransportFailure(Exception ex, CancellationToken cancellationToken) =>
        ex is HttpRequestException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested);

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
}
