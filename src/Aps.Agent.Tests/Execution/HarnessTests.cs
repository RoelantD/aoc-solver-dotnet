using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aps.Agent.Execution;

namespace Aps.Agent.Tests.Execution;

public class HarnessTests
{
    internal static string Marker(object payload) =>
        Harness.MarkerStart + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(payload)) + Harness.MarkerEnd;

    private static string EmbeddedBase64(string harness, string variable) =>
        Encoding.UTF8.GetString(Convert.FromBase64String(
            Regex.Match(harness, variable + @" = .*?b64decode\(""([A-Za-z0-9+/=]*)""\)").Groups[1].Value));

    [Fact]
    public void Build_embeds_program_and_cases_as_base64_and_runs_only_via_exec()
    {
        var code = "def solve(t):\n    return t\n";
        var src = Harness.Build(code, ["a\n", "b\n"]);

        Assert.Contains("exec(compile(_SRC", src); // runs only in the sandbox
        Assert.Equal(code, EmbeddedBase64(src, "_SRC"));
        Assert.Equal(["a\n", "b\n"], JsonSerializer.Deserialize<string[]>(EmbeddedBase64(src, "_CASES"))!);
        Assert.Contains("_LIMIT = 65536", src);
        Assert.Contains("print(\"@@APS_RESULT@@\" + _b64.b64encode", src);
        Assert.DoesNotContain("def solve", src); // the program never appears in clear text
        Assert.DoesNotContain("\r", src);
        Assert.StartsWith("\nimport base64 as _b64", src);
        Assert.EndsWith("+ \"@@END@@\")\n", src);
        Assert.Contains("ns = {\"__name__\": \"solution\"}", src); // braces un-doubled like str.format
    }

    [Fact]
    public void Build_keeps_unicode_and_special_characters_intact()
    {
        var src = Harness.Build("x = 'é<&>\"'", ["☃ \"q\" \\ <tag>"]);
        Assert.Equal("x = 'é<&>\"'", EmbeddedBase64(src, "_SRC"));
        Assert.Equal(["☃ \"q\" \\ <tag>"], JsonSerializer.Deserialize<string[]>(EmbeddedBase64(src, "_CASES"))!);
    }

    [Fact]
    public void Decode_roundtrip_without_execution()
    {
        var payload = new Dictionary<string, object?>
        {
            ["load_error"] = null,
            ["results"] = new[] { new Dictionary<string, object?> { ["index"] = 0, ["output"] = "x", ["error"] = null, ["duration_ms"] = 1 } },
            ["stdout"] = "dbg",
        };
        var stdout = "noise\n" + Marker(payload) + "\n";

        var result = Harness.Decode(stdout);

        Assert.NotNull(result);
        Assert.Null(result.LoadError);
        Assert.Equal(new CaseResult(0, "x", null, 1), Assert.Single(result.Results));
        Assert.Equal("dbg", result.ProgramStdout);
        Assert.Null(Harness.Decode("crashed"));
        Assert.Null(Harness.Decode(null));
    }

    [Fact]
    public void Decode_reads_load_error_and_case_errors()
    {
        var payload = new Dictionary<string, object?>
        {
            ["load_error"] = "SyntaxError: bad",
            ["results"] = Array.Empty<object>(),
            ["stdout"] = "",
        };
        var result = Harness.Decode(Marker(payload));
        Assert.Equal("SyntaxError: bad", result!.LoadError);
        Assert.Empty(result.Results);
    }

    [Fact]
    public void StripMarker_removes_marker_and_trims()
    {
        var stdout = "  hello\n" + Marker(new { load_error = (string?)null, results = Array.Empty<object>(), stdout = "" }) + "\n";
        Assert.Equal("hello", Harness.StripMarker(stdout));
        Assert.Equal("", Harness.StripMarker(null));
    }
}
