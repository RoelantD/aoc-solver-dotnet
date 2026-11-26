using System.Text.RegularExpressions;

namespace Aps.Agent.Verification;

/// <summary>Hardcoded-answer guard: answers must be computed, not written into the code.</summary>
public static class HardcodeGuard
{
    public const int MinAnswerLength = 4;

    /// <summary>True if the computed answer appears as a standalone literal in the source (answers shorter than 4 are not judged).</summary>
    public static bool LooksHardcoded(string code, string? answer)
    {
        var a = (answer ?? "").Trim();
        if (a.Length < MinAnswerLength)
            return false;
        return Regex.IsMatch(code, @"(?<![\w.])" + Regex.Escape(a) + @"(?![\w.])");
    }
}
