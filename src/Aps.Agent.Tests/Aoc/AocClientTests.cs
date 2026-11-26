using System.Net;
using Aps.Agent.Aoc;
using Aps.Agent.Security;
using Aps.Agent.Tests.Fakes;

namespace Aps.Agent.Tests.Aoc;

public sealed class AocClientTests
{
    private const string Token = "tok-123456";

    private static (AocClient Client, FakeClock Clock, FakePuzzleCache Cache) Make(
        HttpMessageHandler handler, double intervalSeconds = 5, string token = Token)
    {
        var clock = new FakeClock();
        var cache = new FakePuzzleCache();
        var options = new AocClientOptions
        {
            Contact = "ops@example.invalid",
            RepoUrl = "https://example.invalid/repo",
            MinInterval = TimeSpan.FromSeconds(intervalSeconds),
        };
        return (new AocClient(new FakeSecretProvider(token), cache, clock, options, handler), clock, cache);
    }

    private static HttpResponseMessage Status(HttpStatusCode status, string? retryAfter = null)
    {
        var response = new HttpResponseMessage(status);
        if (retryAfter is not null)
        {
            response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
        }

        return response;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task PacingCacheAndHeaders()
    {
        var site = FakeAocHandler.WithDay1();
        var seen = new List<(string UserAgent, string Cookie)>();
        var (client, clock, cache) = Make(new FuncHandler(request =>
        {
            seen.Add((request.Headers.UserAgent.ToString(), string.Join("; ", request.Headers.GetValues("Cookie"))));
            return site.HandleAsync(request);
        }));

        await client.CalendarAsync(2024, refresh: true, Ct);
        await client.DayPageAsync(2024, 1, cancellationToken: Ct);
        await client.InputAsync(2024, 1, Ct);
        await client.InputAsync(2024, 1, Ct); // cached
        await client.DayPageAsync(2024, 1, cancellationToken: Ct); // cached

        Assert.Equal(3, seen.Count);
        Assert.Equal([5.0, 5.0], clock.DelaySeconds); // sequential, spaced by the minimum interval
        Assert.Contains("agentic-puzzle-solver/", seen[0].UserAgent);
        Assert.Contains("ops@example.invalid", seen[0].UserAgent);
        Assert.Contains($"session={Token}", seen[0].Cookie);
        Assert.Equal(site.Puzzles[1].Input, cache.Entries[(2024, 1, "input")]);
    }

    [Fact]
    public void IntervalFloor()
    {
        var (client, _, _) = Make(FakeAocHandler.WithDay1(), intervalSeconds: 0.1);
        Assert.Equal(TimeSpan.FromSeconds(1), client.MinInterval);

        client.SetMinInterval(TimeSpan.FromSeconds(3));
        Assert.Equal(TimeSpan.FromSeconds(3), client.MinInterval);
        client.SetMinInterval(TimeSpan.Zero);
        Assert.Equal(TimeSpan.FromSeconds(1), client.MinInterval);
    }

    [Fact]
    public async Task RetryAfterAndBackoffForGet()
    {
        var site = FakeAocHandler.WithDay1();
        var calls = 0;
        var (client, clock, _) = Make(new FuncHandler(request => ++calls switch
        {
            1 => Task.FromResult(Status(HttpStatusCode.TooManyRequests, "7")),
            2 => Task.FromResult(Status(HttpStatusCode.ServiceUnavailable)),
            _ => site.HandleAsync(request),
        }));

        await client.CalendarAsync(2024, refresh: true, Ct);

        Assert.Equal([7.0, 4.0], clock.DelaySeconds.Take(2)); // Retry-After honoured, then exponential backoff
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task GetGivesUpAfterMaxTriesWithCappedBackoff()
    {
        var calls = 0;
        var (client, clock, _) = Make(new FuncHandler(HttpResponseMessage (_) =>
        {
            calls++;
            throw new HttpRequestException(HttpRequestError.ConnectionError, "refused");
        }));

        await Assert.ThrowsAsync<AocException>(() => client.InputAsync(2024, 1, Ct));
        Assert.Equal(AocClient.GetMaxTries, calls);
        // Backoff 2, 4, 8, 16 s; pacing tops up the gaps shorter than the 5 s minimum interval.
        Assert.Equal([2.0, 3.0, 4.0, 1.0, 8.0, 16.0], clock.DelaySeconds);
    }

    [Fact]
    public async Task RetryAfterHttpDateIsHonoured()
    {
        var site = FakeAocHandler.WithDay1();
        var clock = new FakeClock();
        var calls = 0;
        var handler = new FuncHandler(request => ++calls == 1
            ? Task.FromResult(Status(HttpStatusCode.ServiceUnavailable, clock.UtcNow.AddSeconds(30).ToString("r")))
            : site.HandleAsync(request));
        using var client = new AocClient(new FakeSecretProvider(Token), new FakePuzzleCache(), clock, new AocClientOptions(), handler);

        await client.CalendarAsync(2024, cancellationToken: Ct);

        Assert.Equal(30.0, clock.DelaySeconds.First(), precision: 0);
    }

    [Fact]
    public async Task RedirectsAreNotFollowed()
    {
        var calls = 0;
        var (client, _, _) = Make(new FuncHandler(_ =>
        {
            calls++;
            var response = Status(HttpStatusCode.Found);
            response.Headers.Location = new Uri("https://evil.example/steal");
            return response;
        }));

        await Assert.ThrowsAsync<AocAuthException>(() => client.DayPageAsync(2024, 1, cancellationToken: Ct));
        await Assert.ThrowsAsync<AocAuthException>(() => client.InputAsync(2024, 1, Ct));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task UnauthenticatedSession()
    {
        var site = FakeAocHandler.WithDay1();
        site.LoggedIn = false;
        var (client, _, _) = Make(site);

        var status = await client.ValidateSessionAsync(2024, Ct);

        Assert.False(status.LoggedIn);
        await Assert.ThrowsAsync<AocAuthException>(() => client.InputAsync(2024, 1, Ct));
    }

    [Fact]
    public async Task MissingSessionIsReportedWithoutARequest()
    {
        var site = FakeAocHandler.WithDay1();
        var (client, _, _) = Make(site, token: "");

        var status = await client.ValidateSessionAsync(2024, Ct);

        Assert.False(status.LoggedIn);
        Assert.Equal("AOC_SESSION is not configured", status.Detail);
        Assert.Empty(site.Requests);
    }

    [Fact]
    public async Task ValidSessionCachesCalendar()
    {
        var site = FakeAocHandler.WithDay1();
        site.Completed[1] = [1];
        var (client, _, _) = Make(site);
        var fetches = new List<(string Kind, bool Cached)>();
        client.OnFetch = (kind, cached) =>
        {
            fetches.Add((kind, cached));
            return ValueTask.CompletedTask;
        };

        Assert.True((await client.ValidateSessionAsync(2024, Ct)).LoggedIn);
        var stars = await client.CalendarAsync(2024, cancellationToken: Ct);

        Assert.Equal(1, stars[1]);
        Assert.Single(site.Requests);
        Assert.Equal([("calendar", false), ("calendar", true)], fetches);
    }

    [Fact]
    public async Task LockedDayAndRefreshAfterAcceptance()
    {
        var site = FakeAocHandler.WithDay1();
        var (client, _, _) = Make(site);

        await Assert.ThrowsAsync<AocLockedException>(() => client.DayPageAsync(2024, 2, cancellationToken: Ct));
        Assert.Single((await client.DayPageAsync(2024, 1, cancellationToken: Ct)).Parts);
        Assert.Equal(SubmissionOutcome.Correct, (await client.SubmitAsync(2024, 1, 1, "18", Ct)).Outcome);

        Assert.Single((await client.DayPageAsync(2024, 1, cancellationToken: Ct)).Parts); // cache until told otherwise
        Assert.Equal(2, (await client.DayPageAsync(2024, 1, refresh: true, Ct)).Parts.Count);
        Assert.Equal(2, (await client.DayPageAsync(2024, 1, cancellationToken: Ct)).Parts.Count);
    }

    [Fact]
    public async Task PostIsNeverRetried()
    {
        var posts = 0;
        var (client, _, _) = Make(new FuncHandler(HttpResponseMessage (_) =>
        {
            posts++;
            throw new TaskCanceledException("lost", new TimeoutException());
        }));

        await Assert.ThrowsAsync<AocUncertainException>(() => client.SubmitAsync(2024, 1, 1, "18", Ct));
        Assert.Equal(1, posts);

        var (refused, _, _) = Make(new FuncHandler(HttpResponseMessage (_) => throw new HttpRequestException(HttpRequestError.ConnectionError, "refused")));
        await Assert.ThrowsAsync<AocNotSentException>(() => refused.SubmitAsync(2024, 1, 1, "18", Ct));
    }

    [Fact]
    public async Task ConnectionLostAfterServerProcessedIsUncertain()
    {
        var site = FakeAocHandler.WithDay1();
        site.DropNextPostAfterProcessing = true;
        var (client, _, _) = Make(site);

        await Assert.ThrowsAsync<AocUncertainException>(() => client.SubmitAsync(2024, 1, 1, "18", Ct));

        Assert.Single(site.Posts);
        Assert.Contains(1, site.Completed[1]);
    }

    [Fact]
    public async Task SubmitFormAnd429()
    {
        var site = FakeAocHandler.WithDay1();
        var (client, _, _) = Make(site);

        var result = await client.SubmitAsync(2024, 1, 1, "18", Ct);

        Assert.Equal(SubmissionOutcome.Correct, result.Outcome);
        Assert.Equal([(1, 1, "18")], site.Posts);

        var (limited, _, _) = Make(new FuncHandler(_ => Status(HttpStatusCode.TooManyRequests, "30")));
        var cooldown = await limited.SubmitAsync(2024, 1, 1, "1", Ct);
        Assert.Equal(SubmissionOutcome.Cooldown, cooldown.Outcome);
        Assert.Equal(30, cooldown.WaitSeconds);
    }

    [Fact]
    public async Task FakeSiteJudgesAnswersAndSimulatesCooldown()
    {
        var site = FakeAocHandler.WithDay1();
        site.WrongWaitText = "Please wait one minute before trying again.";
        site.CooldownResponses = 1;
        site.CooldownSeconds = 42;
        var (client, _, _) = Make(site);

        var cooldown = await client.SubmitAsync(2024, 1, 1, "18", Ct);
        var tooHigh = await client.SubmitAsync(2024, 1, 1, "99", Ct);
        var locked = await client.SubmitAsync(2024, 1, 2, "287", Ct);

        Assert.Equal((SubmissionOutcome.Cooldown, (int?)42), (cooldown.Outcome, cooldown.WaitSeconds));
        Assert.Equal((SubmissionOutcome.TooHigh, (int?)60), (tooHigh.Outcome, tooHigh.WaitSeconds));
        Assert.Equal(SubmissionOutcome.Locked, locked.Outcome);
        Assert.Equal(3, site.Posts.Count);
    }

    [Fact]
    public async Task SessionCookieIsRegisteredForRedaction()
    {
        const string secret = "registered-session-cookie-0123456789";
        var (client, _, _) = Make(FakeAocHandler.WithDay1(), token: secret);

        await client.ValidateSessionAsync(2024, Ct);

        Assert.Equal($"value {Redactor.Redacted} end", Redactor.RedactText($"value {secret} end"));
    }

    [Fact]
    public async Task InvalidLevelIsRejected()
    {
        var (client, _, _) = Make(FakeAocHandler.WithDay1());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.SubmitAsync(2024, 1, 3, "1", Ct));
    }

    // Allowlist and redaction.

    [Fact]
    public void ServiceUrlAllowlist()
    {
        Assert.NotNull(UrlAllowlist.ValidateServiceUrl("https://x.openai.azure.com/openai/v1"));
        Assert.NotNull(UrlAllowlist.ValidateServiceUrl("https://westeurope.dynamicsessions.io/subscriptions/x"));
        foreach (var bad in (string[])["http://x.openai.azure.com", "https://evil.example.com", "https://u:p@x.openai.azure.com",
                     "https://openai.azure.com.evil.com"])
        {
            Assert.Throws<DisallowedUrlException>(() => UrlAllowlist.ValidateServiceUrl(bad));
        }
    }

    [Fact]
    public void AocUrlAllowlistAndRedirects()
    {
        Assert.True(UrlAllowlist.IsAllowedAocUrl("https://adventofcode.com/2024/day/1"));
        Assert.False(UrlAllowlist.IsAllowedAocUrl("http://adventofcode.com/2024"));
        Assert.False(UrlAllowlist.IsAllowedAocUrl("https://adventofcode.com.evil.io/2024"));

        var from = new Uri("https://adventofcode.com/2024/day/1/answer");
        Assert.Equal("https://adventofcode.com/2024/auth/login", UrlAllowlist.ValidateRedirect(from, "/2024/auth/login").AbsoluteUri);
        Assert.Throws<DisallowedUrlException>(() => UrlAllowlist.ValidateRedirect(from, "https://evil.example/steal"));
        Assert.Throws<DisallowedUrlException>(() => UrlAllowlist.ValidateRedirect(from, "//evil.example/x"));
        Assert.Throws<DisallowedUrlException>(() => UrlAllowlist.ValidateRedirect(from, null));
    }

    [Fact]
    public void Redaction()
    {
        const string secret = "abcdef0123456789session";
        var text = Redactor.RedactText($"Cookie: session={secret}; other  Authorization: Bearer eyJabc.def x-api-key: k123", [secret]);
        Assert.DoesNotContain(secret, text);
        Assert.DoesNotContain("eyJabc", text);
        Assert.DoesNotContain("k123", text);

        var obj = (Dictionary<string, object?>)Redactor.RedactObject(
            new Dictionary<string, object?> { ["headers"] = new Dictionary<string, object?> { ["cookie"] = "session=1", ["x"] = new[] { secret } } },
            [secret])!;
        var headers = (Dictionary<string, object?>)obj["headers"]!;
        Assert.Equal(Redactor.Redacted, headers["cookie"]);
        Assert.Equal([Redactor.Redacted], (List<object?>)headers["x"]!);

        var json = Redactor.RedactJson(System.Text.Json.Nodes.JsonNode.Parse($$"""{"Authorization":"x","msg":"{{secret}}"}"""), [secret])!;
        Assert.Equal($$"""{"Authorization":"{{Redactor.Redacted}}","msg":"{{Redactor.Redacted}}"}""", json.ToJsonString());
    }
}
