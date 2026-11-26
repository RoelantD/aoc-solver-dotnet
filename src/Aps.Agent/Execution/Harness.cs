using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Aps.Agent.Execution;

/// <summary>Decoded harness payload.</summary>
public sealed record HarnessResult(string? LoadError, IReadOnlyList<CaseResult> Results, string ProgramStdout);

/// <summary>
/// Harness protocol. <see cref="Build"/> produces Python source that runs ONLY inside the remote
/// sandbox. This process never executes generated code; it only builds this string and decodes the
/// result marker.
/// </summary>
/// <remarks>
/// The program and the cases are embedded as base64, so no quoting in them can break out of the
/// template. The program is <c>exec</c>'d in its own namespace with stdout captured, each case is a
/// separate <c>solve(text)</c> call whose exceptions are caught and reported, and output is capped.
/// The results come back as one base64 JSON line between two markers, so the program's own prints
/// cannot be confused with them. A missing marker means the process died (crash, memory limit, timeout).
/// </remarks>
public static partial class Harness
{
    public const string MarkerStart = "@@APS_RESULT@@";
    public const string MarkerEnd = "@@END@@";
    public const int OutputLimit = 65536;

    private const string Template = """

import base64 as _b64, json as _json, time as _time, traceback as _tb, io as _io, contextlib as _cl, sys as _sys
_sys.setrecursionlimit(20000)
_SRC = _b64.b64decode("{src}").decode("utf-8")
_CASES = _json.loads(_b64.b64decode("{cases}").decode("utf-8"))
_LIMIT = {limit}

def _aps_err(e):
    lines = _tb.format_exc().splitlines(True)[-20:]
    return (type(e).__name__ + ": " + str(e))[:2000] + "\n" + "".join(lines)[-4000:]

def _aps_run():
    buf = _io.StringIO()
    ns = {"__name__": "solution"}
    try:
        with _cl.redirect_stdout(buf):
            exec(compile(_SRC, "solution.py", "exec"), ns)
    except (Exception, SystemExit) as e:
        return {"load_error": _aps_err(e), "results": [], "stdout": buf.getvalue()[-_LIMIT:]}
    solve = ns.get("solve")
    if not callable(solve):
        return {"load_error": "solve(input_text) is not defined", "results": [], "stdout": buf.getvalue()[-_LIMIT:]}
    results = []
    for i, case in enumerate(_CASES):
        t0 = _time.perf_counter()
        try:
            with _cl.redirect_stdout(buf):
                out = solve(case)
            out = "" if out is None else str(out)
            results.append({"index": i, "output": out[:_LIMIT], "error": None,
                             "duration_ms": int((_time.perf_counter() - t0) * 1000)})
        except (Exception, SystemExit) as e:
            results.append({"index": i, "output": None, "error": _aps_err(e),
                             "duration_ms": int((_time.perf_counter() - t0) * 1000)})
    return {"load_error": None, "results": results, "stdout": buf.getvalue()[-_LIMIT:]}

_aps_result = _aps_run()
print("{start}" + _b64.b64encode(_json.dumps(_aps_result).encode("utf-8")).decode("ascii") + "{end}")

""";

    /// <summary>Builds the harness source: program and cases are embedded as base64, nothing else.</summary>
    public static string Build(string code, IReadOnlyList<string> cases) =>
        Template.ReplaceLineEndings("\n")
            .Replace("{src}", Base64(code))
            .Replace("{cases}", Base64(JsonSerializer.Serialize(cases)))
            .Replace("{limit}", OutputLimit.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Replace("{start}", MarkerStart)
            .Replace("{end}", MarkerEnd);

    /// <summary>Decodes the result marker, or returns null when it is missing (crash, OOM kill or timeout).</summary>
    public static HarnessResult? Decode(string? stdout)
    {
        var match = MarkerRegex().Match(stdout ?? "");
        if (!match.Success)
            return null;
        var json = Encoding.UTF8.GetString(Convert.FromBase64String(match.Groups[1].Value));
        var payload = JsonSerializer.Deserialize<Payload>(json) ?? new Payload();
        return new HarnessResult(
            payload.LoadError,
            [.. (payload.Results ?? []).Select(r => new CaseResult(r.Index, r.Output, r.Error, r.DurationMs))],
            payload.Stdout ?? "");
    }

    /// <summary>Removes the marker line and surrounding whitespace from raw stdout.</summary>
    public static string StripMarker(string? stdout) => MarkerRegex().Replace(stdout ?? "", "").Trim();

    private static string Base64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));

    [GeneratedRegex("@@APS_RESULT@@([A-Za-z0-9+/=]+)@@END@@")]
    private static partial Regex MarkerRegex();

    private sealed class Payload
    {
        [JsonPropertyName("load_error")] public string? LoadError { get; init; }
        [JsonPropertyName("results")] public List<PayloadCase>? Results { get; init; }
        [JsonPropertyName("stdout")] public string? Stdout { get; init; }
    }

    private sealed class PayloadCase
    {
        [JsonPropertyName("index")] public int Index { get; init; }
        [JsonPropertyName("output")] public string? Output { get; init; }
        [JsonPropertyName("error")] public string? Error { get; init; }
        [JsonPropertyName("duration_ms")] public int DurationMs { get; init; }
    }
}
