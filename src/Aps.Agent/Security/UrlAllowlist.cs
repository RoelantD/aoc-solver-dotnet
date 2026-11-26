namespace Aps.Agent.Security;

public sealed class DisallowedUrlException(string message) : ArgumentException(message);

/// <summary>Outbound host allowlist: the agent only talks to Advent of Code and its own Azure services.</summary>
public static class UrlAllowlist
{
    public const string AocOrigin = "https://adventofcode.com";
    public const string AocHost = "adventofcode.com";

    /// <summary>Hosts the agent may call for models and sandbox execution.</summary>
    public static IReadOnlyList<string> ServiceHostSuffixes { get; } =
    [
        ".openai.azure.com",
        ".services.ai.azure.com",
        ".cognitiveservices.azure.com",
        ".dynamicsessions.io",
    ];

    /// <summary>Accepts only https URLs on an allowlisted Azure host suffix, without credentials.</summary>
    /// <exception cref="DisallowedUrlException">The URL is not allowed.</exception>
    public static Uri ValidateServiceUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new DisallowedUrlException($"service URL must use https: '{url}'");
        }

        var host = uri.Host.ToLowerInvariant();
        if (!ServiceHostSuffixes.Any(suffix => host.EndsWith(suffix, StringComparison.Ordinal)))
        {
            throw new DisallowedUrlException($"service host not allowlisted: '{host}'");
        }

        if (uri.UserInfo.Length > 0)
        {
            throw new DisallowedUrlException("credentials in URL are not allowed");
        }

        return uri;
    }

    public static bool IsAllowedAocUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && IsAllowedAocUrl(uri);

    public static bool IsAllowedAocUrl(Uri uri) =>
        uri.IsAbsoluteUri
        && uri.Scheme == Uri.UriSchemeHttps
        && string.Equals(uri.Host, AocHost, StringComparison.OrdinalIgnoreCase)
        && uri.UserInfo.Length == 0;

    /// <summary>
    /// Resolves a redirect <c>Location</c> against the request URI and returns it only when it stays
    /// on the Advent of Code allowlist. The client never follows redirects itself; this is for
    /// deciding how to report one.
    /// </summary>
    /// <exception cref="DisallowedUrlException">Missing, malformed or off-allowlist location.</exception>
    public static Uri ValidateRedirect(Uri requestUri, string? location)
    {
        if (string.IsNullOrWhiteSpace(location) || !Uri.TryCreate(requestUri, location, out var target))
        {
            throw new DisallowedUrlException("redirect without a valid location");
        }

        return IsAllowedAocUrl(target)
            ? target
            : throw new DisallowedUrlException($"redirect to a host that is not allowlisted: '{target.Host}'");
    }
}
