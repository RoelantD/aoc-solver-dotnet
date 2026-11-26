using Aps.Agent.Aoc;
using Aps.Agent.Tests.Fakes;

namespace Aps.Agent.Tests.Aoc;

public sealed class PageParserTests
{
    private static FakeAocHandler Site(Dictionary<int, HashSet<int>>? done = null) => new()
    {
        Puzzles = { [1] = FakePuzzle.SyntheticDay1(), [25] = FakePuzzle.SyntheticDay1() },
        Completed = done ?? [],
    };

    [Fact]
    public void DayPagePart1Only()
    {
        var page = PageParser.ParseDayPage(Site().DayHtml(1));

        Assert.Equal(1, page.Day);
        Assert.Equal("Lantern Ledger", page.Title);
        var part = Assert.Single(page.Parts);
        Assert.Empty(page.CompletedAnswers);
        Assert.Equal(["3 5\n10 4\n7 7\n"], part.PreBlocks);
        Assert.Contains("8", part.Emphasized);
        Assert.Contains("```text\n3 5\n10 4\n7 7\n```", part.Text);
        Assert.Contains("**8**", part.Text);
        Assert.Equal(1, page.FormLevel);
        Assert.Null(page.FormHiddenAnswer);
    }

    [Fact]
    public void DayPageBothPartsComplete()
    {
        var page = PageParser.ParseDayPage(Site(new() { [1] = [1, 2] }).DayHtml(1));

        Assert.Equal(2, page.Parts.Count);
        Assert.Equal(new Dictionary<int, string?> { [1] = "18", [2] = "287" }, page.CompletedAnswers);
    }

    [Fact]
    public void DayPagePart1CompleteShowsPart2AndForm()
    {
        var page = PageParser.ParseDayPage(Site(new() { [1] = [1] }).DayHtml(1));

        Assert.Equal(2, page.Parts.Count);
        Assert.Equal([1], page.CompletedParts);
        Assert.Equal(2, page.FormLevel);
    }

    [Fact]
    public void Day25HiddenAnswer()
    {
        var page = PageParser.ParseDayPage(Site(new() { [25] = [1] }).DayHtml(25));

        Assert.Equal(2, page.FormLevel);
        Assert.Equal("0", page.FormHiddenAnswer);
    }

    [Fact]
    public void LoginDetection()
    {
        var site = Site();
        Assert.True(PageParser.IsLoggedIn(site.CalendarHtml()));

        site.LoggedIn = false;
        Assert.False(PageParser.IsLoggedIn(site.CalendarHtml()));
    }

    [Fact]
    public void CalendarStars()
    {
        var stars = PageParser.ParseCalendarStars(Site(new() { [1] = [1, 2], [3] = [1] }).CalendarHtml());

        Assert.Equal(2, stars[1]);
        Assert.Equal(1, stars[3]);
        Assert.Equal(0, stars[2]);
        Assert.Equal(25, stars.Count);
    }

    [Fact]
    public void RendersHeadingsParagraphsAndLists()
    {
        var page = PageParser.ParseDayPage(
            "<main><article class=\"day-desc\"><h2>--- Day 3: Lists ---</h2><p>Intro <em class=\"star\">x</em></p>"
            + "<ul><li>one <code>a</code></li><li>two</li></ul></article></main>");

        Assert.Equal("## --- Day 3: Lists ---\n\nIntro x\n\n- one `a`\n- two\n", page.Parts[0].Text);
    }

    [Fact]
    public void MainArticleTextIsWhitespaceNormalised()
    {
        Assert.Equal("a b c", PageParser.MainArticleText("<main><article><p>a\n  b</p>\t<p>c</p></article></main>"));
    }
}
