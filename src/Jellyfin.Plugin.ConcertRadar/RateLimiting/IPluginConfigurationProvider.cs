using Jellyfin.Plugin.ConcertRadar.Configuration;

namespace Jellyfin.Plugin.ConcertRadar.RateLimiting;

/// <summary>
/// Provides the current <see cref="PluginConfiguration"/> to rate-limiting infrastructure.
/// Exists as a seam so tests can inject a configuration without going through
/// <see cref="Plugin.Instance"/>.
/// </summary>
public interface IPluginConfigurationProvider
{
    /// <summary>Returns the current plugin configuration, or null if the plugin has not loaded.</summary>
    PluginConfiguration? GetConfiguration();
}
