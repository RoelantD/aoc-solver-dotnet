using Aps.Agent.Verification;

namespace Aps.Agent.Tests.Verification;

public class ExampleVerifierTests
{
    private static readonly string[] Pre = ["3 5\n10 4\n7 7\n"];

    [Fact]
    public void Examples_must_be_verbatim_and_expected_must_be_emphasised()
    {
        var (ok, notes) = ExampleVerifier.Verify(
            [
                new("3 5\n10 4\n7 7", "8", ""),
                new("3 5\n10 4\n7 8\n", "8", ""),
                new("3 5\n", "999", ""),
            ],
            Pre, ["8"], "total drift is `**8**`");

        Assert.Equal(2, ok.Count);
        Assert.True(ok[0].Reliable);
        Assert.Equal("8", ok[0].Expected);
        Assert.Equal("3 5\n10 4\n7 7\n", ok[0].Input);
        Assert.False(ok[1].Reliable);
        Assert.Null(ok[1].Expected);
        Assert.Contains(notes, n => n.Contains("not found verbatim"));
        Assert.Contains("example 3: expected output '999' not found in puzzle text (treated as unreliable)", notes);
    }

    [Fact]
    public void Examples_keep_trailing_whitespace()
    {
        const string text = "ab  \n c \n";
        var (ok, _) = ExampleVerifier.Verify([new(text, null, "")], [text], [], "");
        Assert.Equal(text, ok[0].Input);
    }

    [Fact]
    public void Reliability_from_inline_code_or_bold_and_duplicates_are_dropped()
    {
        var (ok, _) = ExampleVerifier.Verify(
            [new("3 5", "11", "a"), new("3 5\r\n", " 11 ", "dup"), new("10 4", "12", "b")],
            Pre, [], "gives `11` and **12**");

        Assert.Equal(2, ok.Count);
        Assert.True(ok[0].Reliable);
        Assert.Equal("a", ok[0].Note);
        Assert.True(ok[1].Reliable);
        Assert.Equal("12", ok[1].Expected);
    }

    [Fact]
    public void Empty_input_is_rejected_and_max_examples_is_honoured()
    {
        var (ok, notes) = ExampleVerifier.Verify(
            [new("", "1"), new("3 5", null), new("10 4", null), new("7 7", null)],
            Pre, [], "", maxExamples: 2);

        Assert.Equal(["3 5\n", "10 4\n"], ok.Select(e => e.Input));
        Assert.Equal(["example 1 discarded: input not found verbatim in the puzzle"], notes);
    }

    [Fact]
    public void Outputs_match_after_trimming()
    {
        Assert.True(ExampleVerifier.OutputsMatch("42", " 42\n"));
        Assert.False(ExampleVerifier.OutputsMatch("42", "43"));
        Assert.False(ExampleVerifier.OutputsMatch("42", null));
    }
}
