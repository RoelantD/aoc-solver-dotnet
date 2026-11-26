using System.Text.Json.Serialization;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Aps.Agent.Aoc;

/// <summary>
/// Outcome of an answer submission. The JSON names are persisted in the ledger, so they must not change.
/// <see cref="Pending"/> is written before the POST and <see cref="Uncertain"/> when no response arrived.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<SubmissionOutcome>))]
public enum SubmissionOutcome
{
    [JsonStringEnumMemberName("PENDING")] Pending,
    [JsonStringEnumMemberName("UNCERTAIN")] Uncertain,
    [JsonStringEnumMemberName("CORRECT")] Correct,
    [JsonStringEnumMemberName("INCORRECT")] Incorrect,
    [JsonStringEnumMemberName("TOO_HIGH")] TooHigh,
    [JsonStringEnumMemberName("TOO_LOW")] TooLow,
    [JsonStringEnumMemberName("COOLDOWN")] Cooldown,
    [JsonStringEnumMemberName("ALREADY_COMPLETED")] AlreadyCompleted,
    [JsonStringEnumMemberName("LOCKED")] Locked,
    [JsonStringEnumMemberName("AUTH_FAILED")] AuthFailed,
    [JsonStringEnumMemberName("UNEXPECTED")] Unexpected,
}

/// <param name="Message">Normalised article text, at most 1000 characters.</param>
/// <param name="WaitSeconds">Server-imposed wait before the next submission, when stated.</param>
public sealed record SubmitResult(SubmissionOutcome Outcome, string Message, int? WaitSeconds = null);

/// <summary>
/// Classifies answer-submission responses. Advent of Code has no API: the outcome is only in the
/// prose of the returned page, so this matches on its stable phrases.
/// </summary>
public static partial class ResponseClassifier
{
    public const int MaxMessageLength = 1000;

    [GeneratedRegex(@"You have (?:([0-9]+)m )?([0-9]+)s left to wait", RegexOptions.IgnoreCase)]
    private static partial Regex WaitRecentPattern();

    [GeneratedRegex(@"please wait (one|[0-9]+) minutes?", RegexOptions.IgnoreCase)]
    private static partial Regex WaitWrongPattern();

    public static SubmitResult Classify(string html, int statusCode = 200)
    {
        if (statusCode is 301 or 302 or 303 or 307 or 308 || !PageParser.IsLoggedIn(html))
        {
            return new(SubmissionOutcome.AuthFailed, "Not logged in (session invalid or expired).");
        }

        var text = PageParser.MainArticleText(html);
        var message = text.Length > MaxMessageLength ? text[..MaxMessageLength] : text;

        if (text.Contains("That's the right answer", StringComparison.Ordinal)
            || text.Contains("You've finished every puzzle", StringComparison.Ordinal))
        {
            return new(SubmissionOutcome.Correct, message);
        }

        if (text.Contains("Did you already complete it", StringComparison.Ordinal))
        {
            return new(SubmissionOutcome.AlreadyCompleted, message);
        }

        if (text.Contains("You gave an answer too recently", StringComparison.Ordinal))
        {
            var m = WaitRecentPattern().Match(text);
            var wait = m.Success ? (m.Groups[1].Success ? ToInt(m.Groups[1].Value) * 60 : 0) + ToInt(m.Groups[2].Value) : 60;
            return new(SubmissionOutcome.Cooldown, message, wait);
        }

        if (text.Contains("That's not the right answer", StringComparison.Ordinal))
        {
            var m = WaitWrongPattern().Match(text);
            int? wait = !m.Success ? null
                : m.Groups[1].Value.Equals("one", StringComparison.OrdinalIgnoreCase) ? 60
                : ToInt(m.Groups[1].Value) * 60;
            var outcome = text.Contains("answer is too high", StringComparison.OrdinalIgnoreCase) ? SubmissionOutcome.TooHigh
                : text.Contains("answer is too low", StringComparison.OrdinalIgnoreCase) ? SubmissionOutcome.TooLow
                : SubmissionOutcome.Incorrect;
            return new(outcome, message, wait);
        }

        if (text.Contains("You don't seem to be solving the right level", StringComparison.Ordinal))
        {
            return new(SubmissionOutcome.Locked, message);
        }

        return statusCode >= 400
            ? new(SubmissionOutcome.Unexpected, $"HTTP {statusCode}: {(message.Length > 300 ? message[..300] : message)}")
            : new(SubmissionOutcome.Unexpected, message);
    }

    private static int ToInt(string digits) => int.Parse(digits, CultureInfo.InvariantCulture);
}
