namespace Aps.Agent.Verification;

/// <summary>An example as proposed by a model (untrusted until verified).</summary>
public sealed record ProposedExample(string? Input, string? ExpectedOutput, string? Note = null);

/// <summary>An example whose input occurs verbatim in the puzzle.</summary>
public sealed record VerifiedExample(string Input, string? Expected, bool Reliable, string Note);

public sealed record ExampleVerification(IReadOnlyList<VerifiedExample> Accepted, IReadOnlyList<string> Notes);

/// <summary>
/// Accepts model-extracted examples only if they are verbatim, so a hallucinated example cannot steer the tests.
/// </summary>
public static class ExampleVerifier
{
    /// <summary>
    /// The input must occur verbatim (modulo trailing whitespace) in a <c>&lt;pre&gt;</c> block of the
    /// puzzle. The expected output is "reliable" only if it appears as emphasised text or inline code
    /// in the current part's article.
    /// </summary>
    public static ExampleVerification Verify(
        IReadOnlyList<ProposedExample>? proposed,
        IReadOnlyList<string> preBlocks,
        IEnumerable<string> emphasized,
        string articleText,
        int maxExamples = 8)
    {
        var accepted = new List<VerifiedExample>();
        var notes = new List<string>();
        var seen = new HashSet<(string, string?)>();
        var emph = emphasized.Select(e => e.Trim()).ToHashSet(StringComparer.Ordinal);

        for (var i = 0; i < (proposed?.Count ?? 0); i++)
        {
            var ex = proposed![i];
            var rawIn = ex.Input ?? "";
            var exp = string.IsNullOrEmpty(ex.ExpectedOutput) ? null : ex.ExpectedOutput.Trim();
            if (!InBlocks(rawIn, preBlocks))
            {
                notes.Add($"example {i + 1} discarded: input not found verbatim in the puzzle");
                continue;
            }

            var reliable = false;
            if (exp is not null)
            {
                reliable = emph.Contains(exp)
                           || articleText.Contains($"`{exp}`", StringComparison.Ordinal)
                           || articleText.Contains($"**{exp}**", StringComparison.Ordinal);
                if (!reliable)
                    notes.Add($"example {i + 1}: expected output '{exp}' not found in puzzle text (treated as unreliable)");
            }

            if (!seen.Add((Normalize(rawIn), exp)))
                continue;

            // Keep in-line whitespace exactly; some puzzles depend on it.
            var verbatim = rawIn.Replace("\r\n", "\n").Trim('\n') + "\n";
            accepted.Add(new VerifiedExample(verbatim, reliable ? exp : null, reliable, ex.Note ?? ""));
            if (accepted.Count >= maxExamples)
                break;
        }

        return new ExampleVerification(accepted, notes);
    }

    public static bool OutputsMatch(string expected, string? actual) =>
        actual is not null && expected.Trim() == actual.Trim();

    private static string Normalize(string s) =>
        string.Join("\n", s.Replace("\r\n", "\n").Split('\n').Select(line => line.TrimEnd())).Trim('\n');

    private static bool InBlocks(string candidate, IEnumerable<string> blocks)
    {
        var c = Normalize(candidate);
        return c.Length > 0 && blocks.Any(b => Normalize(b).Contains(c, StringComparison.Ordinal));
    }
}
