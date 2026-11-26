using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Web;
using Aps.Agent.Abstractions;

namespace Aps.Agent.Tests.Fakes;

/// <summary>A synthetic puzzle (no real Advent of Code content).</summary>
public sealed record FakePuzzle(string Title, string Part1Html, string Part2Html, string Input, IReadOnlyDictionary<int, string> Answers)
{
    public static FakePuzzle SyntheticDay1() => new(
        "Lantern Ledger",
        "<p>Each line of the ledger lists two lantern readings. The <em>drift</em> of a line is the absolute "
        + "difference between them.</p><p>For example:</p><pre><code>3 5\n10 4\n7 7\n</code></pre>"
        + "<p>The drifts are 2, 6 and 0, so the total drift is <code><em>8</em></code>.</p>"
        + "<p>What is the total drift of your ledger?</p>",
        "<p>Now multiply the readings on each line and add the products.</p>"
        + "<p>In the example, the products are 15, 40 and 49, for a total of <code><em>104</em></code>.</p>"
        + "<p>What is the sum of products?</p>",
        "1 4\n9 2\n5 5\n12 20\n",
        new Dictionary<int, string> { [1] = "18", [2] = "287" });

    public static FakePuzzle SyntheticDay2() => new(
        "Moss Grid",
        "<p>Count the moss tiles (<code>#</code>) in the grid.</p><pre><code>#.#\n.#.\n...\n</code></pre>"
        + "<p>This grid contains <code><em>3</em></code> moss tiles.</p><p>How many moss tiles are there?</p>",
        "<p>Count moss tiles that have at least one moss tile directly above, below, left or right.</p>"
        + "<pre><code>##.\n.#.\n..#\n</code></pre><p>Here <code><em>3</em></code> tiles qualify.</p>",
        "#..#\n##..\n....\n.###\n",
        new Dictionary<int, string> { [1] = "7", [2] = "6" });
}

/// <summary>
/// A stateful fake Advent of Code website, as an <see cref="HttpMessageHandler"/>. It serves the calendar,
/// day pages and inputs, judges POSTed answers against <see cref="FakePuzzle.Answers"/>, simulates
/// cooldowns and lost connections, and records every request and POST.
/// </summary>
public sealed class FakeAocHandler : HttpMessageHandler
{
    private readonly Lock _gate = new();

    public int Year { get; set; } = 2024;

    public Dictionary<int, FakePuzzle> Puzzles { get; } = [];

    /// <summary>Day to completed levels.</summary>
    public Dictionary<int, HashSet<int>> Completed { get; set; } = [];

    public bool LoggedIn { get; set; } = true;

    /// <summary>Appended to wrong-answer responses, e.g. "Please wait one minute before trying again."</summary>
    public string WrongWaitText { get; set; } = "";

    /// <summary>The next N POSTs answer "You gave an answer too recently".</summary>
    public int CooldownResponses { get; set; }

    public int CooldownSeconds { get; set; } = 1;

    /// <summary>Process the next correct POST, then drop the connection before the response arrives.</summary>
    public bool DropNextPostAfterProcessing { get; set; }

    public ConcurrentQueue<(string Method, string Path)> Requests { get; } = new();

    public ConcurrentQueue<(int Day, int Level, string Answer)> Posts { get; } = new();

    /// <summary>A site with <see cref="FakePuzzle.SyntheticDay1"/> as day 1 (and optionally more).</summary>
    public static FakeAocHandler WithDay1() => new() { Puzzles = { [1] = FakePuzzle.SyntheticDay1() } };

    public string CalendarHtml()
    {
        var links = new StringBuilder();
        for (var d = 1; d <= 25; d++)
        {
            var n = Completed.TryGetValue(d, out var done) ? done.Count : 0;
            var cls = $"calendar-day{d}" + (n >= 2 ? " calendar-verycomplete" : n == 1 ? " calendar-complete" : "");
            links.Append($"<a aria-label=\"Day {d}\" href=\"/{Year}/day/{d}\" class=\"{cls}\">Day {d}</a>");
        }

        return $"<html><body>{Nav()}<main><pre class=\"calendar\">{links}</pre></main></body></html>";
    }

    public string DayHtml(int day)
    {
        var pz = Puzzles[day];
        var done = Completed.GetValueOrDefault(day) ?? [];
        var body = new StringBuilder(
            $"<article class=\"day-desc\"><h2>--- Day {day}: {Esc(pz.Title)} ---</h2>{pz.Part1Html}</article>");
        if (done.Contains(1))
        {
            body.Append($"<p>Your puzzle answer was <code>{Esc(pz.Answers[1])}</code>.</p>");
            if (day != 25)
            {
                body.Append($"<article class=\"day-desc\"><h2 id=\"part2\">--- Part Two ---</h2>{pz.Part2Html}</article>");
            }

            if (done.Contains(2))
            {
                if (day != 25)
                {
                    body.Append($"<p>Your puzzle answer was <code>{Esc(pz.Answers[2])}</code>.</p>");
                }

                body.Append("<p class=\"day-success\">Both parts of this puzzle are complete! They provide two gold stars: **</p>");
            }
            else
            {
                body.Append(day == 25
                    ? "<form method=\"post\" action=\"25/answer\"><input type=\"hidden\" name=\"level\" value=\"2\"/>"
                      + "<input type=\"hidden\" name=\"answer\" value=\"0\"/></form>"
                    : $"<form method=\"post\" action=\"{day}/answer\"><input type=\"hidden\" name=\"level\" "
                      + "value=\"2\"/><input type=\"text\" name=\"answer\"/></form>");
                body.Append("<p>The first half of this puzzle is complete! It provides one gold star: *</p>");
            }
        }
        else
        {
            body.Append($"<form method=\"post\" action=\"{day}/answer\"><input type=\"hidden\" name=\"level\" value=\"1\"/>"
                + "<input type=\"text\" name=\"answer\"/></form>");
        }

        return $"<html><body>{Nav()}<main>{body}</main></body></html>";
    }

    public string AnswerHtml(string text) =>
        $"<html><body>{Nav()}<main><article><p>{text}</p></article></main></body></html>";

    /// <summary>Handles one request; public so tests can wrap the site in their own handler.</summary>
    public async Task<HttpResponseMessage> HandleAsync(HttpRequestMessage request, CancellationToken cancellationToken = default)
    {
        var form = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        lock (_gate)
        {
            return Handle(request, form);
        }
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        HandleAsync(request, cancellationToken);

    private HttpResponseMessage Handle(HttpRequestMessage request, string form)
    {
        var path = request.RequestUri!.AbsolutePath;
        var method = request.Method.Method;
        Requests.Enqueue((method, path));
        var cookie = request.Headers.TryGetValues("Cookie", out var values) ? string.Join("; ", values) : "";
        var authed = cookie.Contains("session=", StringComparison.Ordinal) && LoggedIn;
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (method == "GET" && parts is [var y] && y == Year.ToString(System.Globalization.CultureInfo.InvariantCulture))
        {
            return Html(HttpStatusCode.OK, CalendarHtml());
        }

        if (parts.Length >= 3 && parts[1] == "day" && int.TryParse(parts[2], out var day))
        {
            if (!Puzzles.ContainsKey(day))
            {
                return Html(HttpStatusCode.NotFound, "Not found");
            }

            switch (method, parts[3..])
            {
                case ("GET", []):
                    return Html(HttpStatusCode.OK, DayHtml(day));
                case ("GET", ["input"]):
                    return authed
                        ? Html(HttpStatusCode.OK, Puzzles[day].Input)
                        : Html(HttpStatusCode.BadRequest, "Puzzle inputs differ by user.  Please log in to get your puzzle input.");
                case ("POST", ["answer"]):
                    return Answer(day, form);
            }
        }

        return Html(HttpStatusCode.NotFound, "Not found");
    }

    private HttpResponseMessage Answer(int day, string formBody)
    {
        var form = HttpUtility.ParseQueryString(formBody);
        var level = int.TryParse(form["level"], out var l) ? l : 0;
        var answer = form["answer"] ?? "";
        Posts.Enqueue((day, level, answer));

        if (!LoggedIn)
        {
            var redirect = new HttpResponseMessage(HttpStatusCode.Found);
            redirect.Headers.Location = new Uri($"/{Year}/auth/login", UriKind.Relative);
            return redirect;
        }

        if (CooldownResponses > 0)
        {
            CooldownResponses--;
            return Html(HttpStatusCode.OK, AnswerHtml(
                "You gave an answer too recently; you have to wait after submitting an answer before trying again. "
                + $"You have {CooldownSeconds}s left to wait. [Return to Day {day}]"));
        }

        if (!Completed.TryGetValue(day, out var done))
        {
            Completed[day] = done = [];
        }

        if (done.Contains(level))
        {
            return Html(HttpStatusCode.OK, AnswerHtml(
                $"You don't seem to be solving the right level. Did you already complete it? [Return to Day {day}]"));
        }

        if (level == 2 && !done.Contains(1))
        {
            return Html(HttpStatusCode.OK, AnswerHtml($"You don't seem to be solving the right level. [Return to Day {day}]"));
        }

        var expected = Puzzles[day].Answers[level];
        if (answer.Trim() == expected)
        {
            done.Add(level);
            if (DropNextPostAfterProcessing)
            {
                DropNextPostAfterProcessing = false;
                throw new HttpRequestException(HttpRequestError.ResponseEnded, "connection reset after server processed request");
            }

            return Html(HttpStatusCode.OK, AnswerHtml(
                "That's the right answer! You are <span class=\"day-success\">one gold star</span> closer. [Continue to Part Two]"));
        }

        var hint = long.TryParse(answer, out var given) && long.TryParse(expected, out var want)
            ? given > want ? "; your answer is too high" : "; your answer is too low"
            : "";
        return Html(HttpStatusCode.OK, AnswerHtml(
            $"That's not the right answer{hint}. If you're stuck, make sure you're using the full input data. "
            + $"{WrongWaitText} (You guessed <span style=\"white-space:nowrap;\"><code>{Esc(answer)}"
            + $"</code>.)</span> [Return to Day {day}]"));
    }

    private string Nav() => LoggedIn
        ? "<header><div class=\"user\">Synthetic Tester <span class=\"star-count\">0*</span></div></header>"
        : $"<header><a href=\"/{Year}/auth/login\">[Log In]</a></header>";

    private static string Esc(string s) => WebUtility.HtmlEncode(s);

    private static HttpResponseMessage Html(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "text/html") };
}

/// <summary>An <see cref="HttpMessageHandler"/> backed by a delegate, for one-off responses or wrapping the fake site.</summary>
public sealed class FuncHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handle) : HttpMessageHandler
{
    public FuncHandler(Func<HttpRequestMessage, HttpResponseMessage> handle)
        : this(request => Task.FromResult(handle(request)))
    {
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        handle(request);
}

/// <summary>A minimal in-memory <see cref="IPuzzleCache"/>.</summary>
public sealed class FakePuzzleCache : IPuzzleCache
{
    public ConcurrentDictionary<(int Year, int Day, string Kind), string> Entries { get; } = new();

    public ValueTask<string?> GetAsync(int year, int day, string kind, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Entries.TryGetValue((year, day, kind), out var v) ? v : null);

    public ValueTask SetAsync(int year, int day, string kind, string content, CancellationToken cancellationToken = default)
    {
        Entries[(year, day, kind)] = content;
        return ValueTask.CompletedTask;
    }
}

/// <summary>An <see cref="ISecretProvider"/> returning a fixed session value.</summary>
public sealed class FakeSecretProvider(string session) : ISecretProvider
{
    public ValueTask<string> GetAocSessionAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(session);
}
