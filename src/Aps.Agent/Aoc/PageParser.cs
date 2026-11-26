using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace Aps.Agent.Aoc;

/// <summary>One part's article from a day page.</summary>
/// <param name="Index">1-based part number.</param>
/// <param name="Html">The article's outer HTML.</param>
/// <param name="Text">Model-facing rendering with verbatim code blocks.</param>
/// <param name="PreBlocks">Text of every <c>pre</c> block, verbatim.</param>
/// <param name="Emphasized">Emphasised values (candidate example answers).</param>
public sealed record PartArticle(
    int Index,
    string Html,
    string Text,
    IReadOnlyList<string> PreBlocks,
    IReadOnlyList<string> Emphasized);

public sealed record DayPage(
    int? Day,
    string? Title,
    IReadOnlyList<PartArticle> Parts,
    IReadOnlyDictionary<int, string?> CompletedAnswers,
    bool LoggedIn = true,
    int? FormLevel = null,
    string? FormHiddenAnswer = null)
{
    public IReadOnlySet<int> CompletedParts => CompletedAnswers.Keys.ToHashSet();
}

/// <summary>
/// HTML parsing for Advent of Code pages.
/// All page content is untrusted data; parsing never executes or follows anything.
/// </summary>
public static partial class PageParser
{
    [GeneratedRegex(@"---\s*Day\s+([0-9]+):\s*(.*?)\s*---")]
    private static partial Regex TitlePattern();

    [GeneratedRegex(@"^/\d{4}/auth/login\z")]
    private static partial Regex LoginHrefPattern();

    [GeneratedRegex(@"^calendar-day([0-9]+)\z")]
    private static partial Regex CalendarDayPattern();

    [GeneratedRegex("post", RegexOptions.IgnoreCase)]
    private static partial Regex PostPattern();

    public static bool IsLoggedIn(string html) => IsLoggedIn(Parse(html));

    public static DayPage ParseDayPage(string html)
    {
        var doc = Parse(html);
        var parts = new List<PartArticle>();
        int? day = null;
        string? title = null;
        foreach (var article in doc.QuerySelectorAll("article.day-desc"))
        {
            if (parts.Count == 0 && article.QuerySelector("h2") is { } h2
                && TitlePattern().Match(h2.TextContent) is { Success: true } m)
            {
                day = int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                title = m.Groups[2].Value;
            }

            parts.Add(new PartArticle(
                parts.Count + 1,
                article.OuterHtml,
                RenderArticle(article),
                [.. article.QuerySelectorAll("pre").Select(pre => pre.TextContent)],
                EmphasizedValues(article)));
        }

        var completed = new Dictionary<int, string?>();
        foreach (var p in doc.QuerySelectorAll("main p"))
        {
            if (p.TextContent.Trim().StartsWith("Your puzzle answer was", StringComparison.Ordinal))
            {
                completed[completed.Count + 1] = p.QuerySelector("code")?.TextContent.Trim();
            }
        }

        var text = GetText(doc.DocumentElement, " ");
        if (text.Contains("Both parts of this puzzle are complete", StringComparison.Ordinal))
        {
            completed.TryAdd(1, null);
            completed.TryAdd(2, null);
        }
        else if (text.Contains("The first half of this puzzle is complete", StringComparison.Ordinal))
        {
            completed.TryAdd(1, null);
        }

        int? formLevel = null;
        string? formHiddenAnswer = null;
        var form = doc.QuerySelectorAll("form")
            .FirstOrDefault(f => f.GetAttribute("method") is { } method && PostPattern().IsMatch(method));
        if (form is not null)
        {
            var level = form.QuerySelector("input[name='level']")?.GetAttribute("value") ?? "";
            if (level.Length > 0 && level.All(char.IsDigit) && int.TryParse(level, out var parsed))
            {
                formLevel = parsed;
            }

            var answer = form.QuerySelector("input[name='answer']");
            if (answer is not null
                && string.Equals(answer.GetAttribute("type"), "hidden", StringComparison.OrdinalIgnoreCase))
            {
                formHiddenAnswer = answer.GetAttribute("value") ?? "";
            }
        }

        return new DayPage(day, title, parts, completed, IsLoggedIn(doc), formLevel, formHiddenAnswer);
    }

    /// <summary>Day to stars (0, 1, 2) from the year calendar's link classes.</summary>
    public static IReadOnlyDictionary<int, int> ParseCalendarStars(string html)
    {
        var stars = new Dictionary<int, int>();
        foreach (var a in Parse(html).QuerySelectorAll("a"))
        {
            int? day = null;
            foreach (var cls in a.ClassList)
            {
                if (CalendarDayPattern().Match(cls) is { Success: true } m)
                {
                    day = int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                }
            }

            if (day is { } d)
            {
                stars[d] = a.ClassList.Contains("calendar-verycomplete") ? 2
                    : a.ClassList.Contains("calendar-complete") ? 1
                    : 0;
            }
        }

        return stars;
    }

    /// <summary>Whitespace-normalised text of the page's main article (used to classify submissions).</summary>
    public static string MainArticleText(string html)
    {
        var doc = Parse(html);
        INode node = doc.QuerySelector("main article")
            ?? doc.QuerySelector("article")
            ?? doc.QuerySelector("main")
            ?? (INode)doc;
        return string.Join(' ', GetText(node, " ").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Renders an article as Markdown-ish text: headings, paragraphs, lists and fenced code blocks.</summary>
    public static string RenderArticle(IElement article)
    {
        var output = new StringBuilder();
        foreach (var child in article.ChildNodes)
        {
            RenderBlock(child, output);
        }

        return output.ToString().Trim() + "\n";
    }

    private static IDocument Parse(string html) => new HtmlParser().ParseDocument(html);

    private static bool IsLoggedIn(IDocument doc) =>
        !doc.QuerySelectorAll("a[href]").Any(a =>
            LoginHrefPattern().IsMatch(a.GetAttribute("href")!) || a.TextContent.Contains("[Log In]", StringComparison.Ordinal));

    private static List<string> EmphasizedValues(IElement article)
    {
        var values = article.QuerySelectorAll("em").Select(em => em.TextContent.Trim()).Where(t => t.Length > 0).ToList();
        values.AddRange(article.QuerySelectorAll("code")
            .Where(code => code.QuerySelector("em") is not null)
            .Select(code => code.TextContent.Trim())
            .Where(t => t.Length > 0));
        return values;
    }

    private static string Inline(INode node)
    {
        if (node is IText text)
        {
            return text.Data;
        }

        if (node is not IElement element)
        {
            return "";
        }

        var inner = string.Concat(element.ChildNodes.Select(Inline));
        return element.LocalName switch
        {
            "code" => $"`{inner}`",
            "em" => element.ClassList.Contains("star") ? inner : $"**{inner}**",
            "br" => "\n",
            _ => inner,
        };
    }

    private static void RenderBlock(INode node, StringBuilder output)
    {
        if (node is IText text)
        {
            var trimmed = text.Data.Trim();
            if (trimmed.Length > 0)
            {
                output.Append(trimmed).Append("\n\n");
            }

            return;
        }

        if (node is not IElement element)
        {
            return;
        }

        switch (element.LocalName)
        {
            case "h2":
                output.Append("## ").Append(element.TextContent.Trim()).Append("\n\n");
                break;
            case "pre":
                output.Append("```text\n").Append(element.TextContent.TrimEnd('\n')).Append("\n```\n\n");
                break;
            case "ul" or "ol":
                foreach (var li in element.Children.Where(c => c.LocalName == "li"))
                {
                    output.Append("- ").Append(Inline(li).Trim()).Append('\n');
                }

                output.Append('\n');
                break;
            case "p":
                output.Append(Inline(element).Trim()).Append("\n\n");
                break;
            default:
                foreach (var child in element.ChildNodes)
                {
                    RenderBlock(child, output);
                }

                break;
        }
    }

    /// <summary>All descendant text joined by <paramref name="separator"/>.</summary>
    private static string GetText(INode? node, string separator) =>
        node is null ? "" : string.Join(separator, node.GetDescendants().OfType<IText>().Select(t => t.Data));
}
