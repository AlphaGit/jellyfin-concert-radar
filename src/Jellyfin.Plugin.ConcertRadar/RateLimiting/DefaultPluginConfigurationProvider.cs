using Jellyfin.Plugin.ConcertRadar.Configuration;

namespace Jellyfin.Plugin.ConcertRadar.RateLimiting;

/// <summary>
/// Production implementation of <see cref="IPluginConfigurationProvider"/> that reads from
/// <see cref="Plugin.Instance"/>.
/// </summary>
public sealed class DefaultPluginConfigurationProvider : IPluginConfigurationProvider
{
    /// <inheritdoc />
    public PluginConfiguration? GetConfiguration() => Plugin.Instance?.Configuration;
}
