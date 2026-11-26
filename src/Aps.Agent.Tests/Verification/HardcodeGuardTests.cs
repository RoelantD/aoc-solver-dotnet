using Aps.Agent.Verification;

namespace Aps.Agent.Tests.Verification;

public class HardcodeGuardTests
{
    [Theory]
    [InlineData("def solve(t):\n    return '123456'\n", "123456", true)]
    [InlineData("def solve(t):\n    return 123456\n", " 123456\n", true)]
    [InlineData("def solve(t):\n    return str(sum(x))\n", "123456", false)]
    [InlineData("x = 18\n", "18", false)] // too short to judge
    [InlineData("x = 1234567\n", "123456", false)]
    [InlineData("x = 1.123456\n", "123456", false)]
    [InlineData("x = abc1234\n", "1234", false)]
    [InlineData("ANSWER = 'a.b+c'\n", "a.b+c", true)] // regex metacharacters are literal
    public void Detects_answer_literals_in_code(string code, string answer, bool expected) =>
        Assert.Equal(expected, HardcodeGuard.LooksHardcoded(code, answer));
}
