using Aps.Agent.Abstractions;
using Azure.Core;
using Azure.Security.KeyVault.Secrets;

namespace Aps.Agent.Security;

/// <summary>
/// Reads the Advent of Code session cookie from Key Vault with the agent's own identity, so the cookie
/// never lives in the hosted agent's configuration or environment variables.
/// </summary>
public sealed class KeyVaultSecretProvider(Uri vaultUri, TokenCredential credential) : ISecretProvider
{
    public const string AocSessionSecretName = "aoc-session";

    private readonly SecretClient _client = new(vaultUri, credential);
    private string? _cached;
    private DateTimeOffset _cachedAt;

    public async ValueTask<string> GetAocSessionAsync(CancellationToken cancellationToken = default)
    {
        // Short cache: rotating the secret takes effect within five minutes without a redeploy.
        if (_cached is not null && DateTimeOffset.UtcNow - _cachedAt < TimeSpan.FromMinutes(5)) return _cached;
        var secret = await _client.GetSecretAsync(AocSessionSecretName, cancellationToken: cancellationToken);
        _cached = secret.Value.Value;
        _cachedAt = DateTimeOffset.UtcNow;
        return _cached;
    }
}

/// <summary>
/// Development-only provider: reads <c>AOC_SESSION</c> from the environment or the ignored <c>.env</c> file
/// next to the repository root. Never used when <c>KEYVAULT_URI</c> is configured.
/// </summary>
public sealed class DevelopmentSecretProvider(string? envFile = null) : ISecretProvider
{
    public ValueTask<string> GetAocSessionAsync(CancellationToken cancellationToken = default)
    {
        var value = Environment.GetEnvironmentVariable("AOC_SESSION");
        if (string.IsNullOrWhiteSpace(value) && envFile is not null && File.Exists(envFile))
        {
            value = File.ReadLines(envFile)
                .Select(l => l.Trim())
                .Where(l => l.StartsWith("AOC_SESSION=", StringComparison.Ordinal))
                .Select(l => l["AOC_SESSION=".Length..].Trim('"'))
                .FirstOrDefault();
        }
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException("AOC_SESSION is not configured (set KEYVAULT_URI in Azure, or AOC_SESSION locally)")
            : ValueTask.FromResult(value);
    }
}
