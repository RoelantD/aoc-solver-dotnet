using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Aps.Agent.Security;

/// <summary>
/// Secret and token redaction for logs, events, telemetry and persisted data.
/// Secret values read at runtime (for example the AoC session cookie) are registered once and then
/// scrubbed from every string that passes through <see cref="RedactText"/>.
/// </summary>
public static partial class Redactor
{
    public const string Redacted = "***REDACTED***";

    /// <summary>Shorter values are not treated as secrets; replacing them would mangle ordinary text.</summary>
    public const int MinSecretLength = 6;

    private static readonly HashSet<string> SensitiveKeys =
        new(StringComparer.OrdinalIgnoreCase) { "cookie", "authorization", "api-key", "x-api-key", "aoc_session" };

    private static readonly ConcurrentDictionary<string, byte> Registered = new(StringComparer.Ordinal);

    [GeneratedRegex("""(session=)[^;\s"']+""", RegexOptions.IgnoreCase)]
    private static partial Regex SessionPattern();

    [GeneratedRegex("""(authorization["']?\s*[:=]\s*["']?bearer\s+)[A-Za-z0-9\-._~+/]+=*""", RegexOptions.IgnoreCase)]
    private static partial Regex BearerPattern();

    [GeneratedRegex("""((?:x-)?api-key["']?\s*[:=]\s*["']?)[^\s"',}]+""", RegexOptions.IgnoreCase)]
    private static partial Regex ApiKeyPattern();

    /// <summary>Registers a secret value so it is redacted everywhere from now on. Null or short values are ignored.</summary>
    public static void RegisterSecret(string? secret)
    {
        if (secret is { Length: >= MinSecretLength })
        {
            Registered.TryAdd(secret, 0);
        }
    }

    /// <summary>Replaces registered secrets, <paramref name="secrets"/>, session cookies, bearer tokens and API keys.</summary>
    [return: NotNullIfNotNull(nameof(text))]
    public static string? RedactText(string? text, IEnumerable<string>? secrets = null)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var output = text;
        // Longest first, so a secret that contains another is removed whole.
        foreach (var secret in AllSecrets(secrets).OrderByDescending(s => s.Length))
        {
            output = output.Replace(secret, Redacted, StringComparison.Ordinal);
        }

        foreach (var pattern in (Regex[])[SessionPattern(), BearerPattern(), ApiKeyPattern()])
        {
            output = pattern.Replace(output, m => m.Groups[1].Value + Redacted);
        }

        return output;
    }

    /// <summary>
    /// Redacts strings inside dictionaries and sequences (recursively). Values under sensitive keys
    /// (cookie, authorization, api-key, x-api-key, aoc_session) are replaced entirely. Dictionaries
    /// come back as <c>Dictionary&lt;string, object?&gt;</c> and sequences as <c>List&lt;object?&gt;</c>.
    /// </summary>
    public static object? RedactObject(object? value, IEnumerable<string>? secrets = null)
    {
        var list = secrets?.ToList();
        return value switch
        {
            null => null,
            string s => RedactText(s, list),
            JsonNode node => RedactJson(node, list),
            IDictionary dict => dict.Keys.Cast<object>().ToDictionary(
                k => Convert.ToString(k, System.Globalization.CultureInfo.InvariantCulture) ?? "",
                k => IsSensitiveKey(k) ? Redacted : RedactObject(dict[k], list)),
            IEnumerable items => items.Cast<object?>().Select(v => RedactObject(v, list)).ToList(),
            _ => value,
        };
    }

    /// <summary>Returns a redacted deep copy of a JSON tree.</summary>
    public static JsonNode? RedactJson(JsonNode? node, IEnumerable<string>? secrets = null)
    {
        var list = secrets?.ToList();
        switch (node)
        {
            case null:
                return null;
            case JsonObject obj:
                var copy = new JsonObject();
                foreach (var (key, child) in obj)
                {
                    copy[key] = IsSensitiveKey(key) ? JsonValue.Create(Redacted) : RedactJson(child, list);
                }

                return copy;
            case JsonArray array:
                return new JsonArray([.. array.Select(child => RedactJson(child, list))]);
            case JsonValue v when v.TryGetValue<string>(out var s):
                return JsonValue.Create(RedactText(s, list));
            default:
                return node.DeepClone();
        }
    }

    private static bool IsSensitiveKey(object? key) =>
        SensitiveKeys.Contains(Convert.ToString(key, System.Globalization.CultureInfo.InvariantCulture) ?? "");

    private static IEnumerable<string> AllSecrets(IEnumerable<string>? extra) =>
        Registered.Keys
            .Concat((extra ?? []).Where(s => s is { Length: >= MinSecretLength }))
            .Distinct(StringComparer.Ordinal);
}
