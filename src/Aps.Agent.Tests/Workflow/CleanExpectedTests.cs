using Aps.Agent.Workflow;

namespace Aps.Agent.Tests.Workflow;

public sealed class CleanExpectedTests
{
    [Theory]
    [InlineData(">8", "8")]
    [InlineData("<em>8</em>", "8")]
    [InlineData("`104`", "104")]
    [InlineData(" -12 ", "-12")]
    [InlineData("ABC,DEF", "ABC,DEF")]
    [InlineData(null, null)]
    public void Removes_markup_remnants_only(string? input, string? expected) =>
        Assert.Equal(expected, VerifyExamplesExecutor.CleanExpected(input));
}
