using Jellyfin.Plugin.ConcertRadar.Configuration;
using Jellyfin.Plugin.ConcertRadar.RateLimiting;

namespace Jellyfin.Plugin.ConcertRadar.Sources;

/// <summary>
/// Builds the outgoing <c>User-Agent</c> header used by every adapter and the MusicBrainz
/// resolver. Some upstream services (notably MusicBrainz, Bandsintown) require a meaningful
/// User-Agent that includes a contact URL or email so abuse reports can reach the operator.
///
/// The contact string is operator-supplied via <see cref="PluginConfiguration.UserAgentContact"/>.
/// When unset, a neutral fallback identifying only the plugin name + version is emitted —
/// this never references the upstream project's repository.
/// </summary>
internal static class UserAgentBuilder
{
    /// <summary>Plugin name advertised in the User-Agent.</summary>
    public const string ProductName = "JellyfinConcertRadar";

    /// <summary>
    /// Fallback version used when <see cref="Plugin.Instance"/> is unavailable (e.g. in unit
    /// tests that don't construct the full plugin host). Production callers resolve the
    /// version from <c>Plugin.Instance.Version</c>.
    /// </summary>
    private const string FallbackVersion = "0.1.1";

    /// <summary>Resolves the plugin version at call time so build.yaml is the single source of truth.</summary>
    public static string ProductVersion
        => Plugin.Instance?.Version?.ToString(fieldCount: 3) ?? FallbackVersion;

    /// <summary>Builds the API-style User-Agent (used by MusicBrainz, Bandsintown, EdmTrain).</summary>
    public static string BuildApi(IPluginConfigurationProvider provider)
        => BuildApi(provider.GetConfiguration()?.UserAgentContact);

    /// <summary>Builds the API-style User-Agent for a known contact value.</summary>
    public static string BuildApi(string? contact)
    {
        var trimmed = contact?.Trim();
        return string.IsNullOrEmpty(trimmed)
            ? $"{ProductName}/{ProductVersion}"
            : $"{ProductName}/{ProductVersion} ( {trimmed} )";
    }

    /// <summary>Builds the browser-style User-Agent (used by scrape adapters).</summary>
    public static string BuildBrowser(IPluginConfigurationProvider provider)
        => BuildBrowser(provider.GetConfiguration()?.UserAgentContact);

    /// <summary>Builds the browser-style User-Agent for a known contact value.</summary>
    public static string BuildBrowser(string? contact)
    {
        var trimmed = contact?.Trim();
        return string.IsNullOrEmpty(trimmed)
            ? $"Mozilla/5.0 (compatible; {ProductName}/{ProductVersion})"
            : $"Mozilla/5.0 (compatible; {ProductName}/{ProductVersion}; +{trimmed})";
    }
}
