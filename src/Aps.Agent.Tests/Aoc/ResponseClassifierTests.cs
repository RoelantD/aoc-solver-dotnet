using Aps.Agent.Aoc;
using Aps.Agent.Tests.Fakes;

namespace Aps.Agent.Tests.Aoc;

public sealed class ResponseClassifierTests
{
    private static string Response(string text, bool loggedIn = true) => new FakeAocHandler { LoggedIn = loggedIn }.AnswerHtml(text);

    [Theory]
    [InlineData("That's the right answer! You are one gold star closer. [Continue to Part Two]", SubmissionOutcome.Correct, null)]
    [InlineData("That's not the right answer; your answer is too high. If you're stuck... Please wait one minute before "
        + "trying again. [Return to Day 1]", SubmissionOutcome.TooHigh, 60)]
    [InlineData("That's not the right answer; your answer is too low. Because you have guessed incorrectly 4 times on this "
        + "puzzle, please wait 5 minutes before trying again.", SubmissionOutcome.TooLow, 300)]
    [InlineData("That's not the right answer. If you're stuck, make sure you're using the full input data.", SubmissionOutcome.Incorrect, null)]
    [InlineData("You gave an answer too recently; you have to wait after submitting an answer before trying again.  "
        + "You have 3m 30s left to wait. [Return to Day 1]", SubmissionOutcome.Cooldown, 210)]
    [InlineData("You gave an answer too recently; you have to wait after submitting an answer before trying again. "
        + "You have 34s left to wait.", SubmissionOutcome.Cooldown, 34)]
    [InlineData("You gave an answer too recently.", SubmissionOutcome.Cooldown, 60)]
    [InlineData("You don't seem to be solving the right level.  Did you already complete it? [Return to Day 1]",
        SubmissionOutcome.AlreadyCompleted, null)]
    [InlineData("You don't seem to be solving the right level. [Return to Day 1]", SubmissionOutcome.Locked, null)]
    [InlineData("Congratulations! You've finished every puzzle in Advent of Code 2024!", SubmissionOutcome.Correct, null)]
    [InlineData("Something entirely new.", SubmissionOutcome.Unexpected, null)]
    public void Classification(string text, SubmissionOutcome outcome, int? wait)
    {
        var result = ResponseClassifier.Classify(Response(text));

        Assert.Equal(outcome, result.Outcome);
        Assert.Equal(wait, result.WaitSeconds);
    }

    [Fact]
    public void AuthFailure()
    {
        Assert.Equal(SubmissionOutcome.AuthFailed, ResponseClassifier.Classify(Response("anything", loggedIn: false)).Outcome);
        Assert.Equal(SubmissionOutcome.AuthFailed, ResponseClassifier.Classify("", 302).Outcome);
    }

    [Fact]
    public void Http200AloneIsNotAcceptance()
    {
        Assert.Equal(
            SubmissionOutcome.Unexpected,
            ResponseClassifier.Classify("<html><main><article><p>OK</p></article></main></html>", 200).Outcome);
    }

    [Fact]
    public void ServerErrorIsUnexpectedWithStatus()
    {
        var result = ResponseClassifier.Classify("<main><article><p>Oops</p></article></main>", 500);

        Assert.Equal(SubmissionOutcome.Unexpected, result.Outcome);
        Assert.Equal("HTTP 500: Oops", result.Message);
    }

    [Fact]
    public void MessageIsNormalisedAndCapped()
    {
        var result = ResponseClassifier.Classify(Response("That's the right answer!   " + new string('x', 2000)));

        Assert.Equal(ResponseClassifier.MaxMessageLength, result.Message.Length);
        Assert.StartsWith("That's the right answer! x", result.Message);
    }
}
