using Jellyfin.Plugin.ConcertRadar.Configuration;
using Jellyfin.Plugin.ConcertRadar.RateLimiting;

namespace Jellyfin.Plugin.ConcertRadar.Tests.Support;

/// <summary>
/// Test stub for <see cref="IPluginConfigurationProvider"/> that returns a
/// pre-configured <see cref="PluginConfiguration"/> without going through
/// <c>Plugin.Instance</c>.
/// </summary>
internal sealed class StubPluginConfigurationProvider : IPluginConfigurationProvider
{
    private readonly PluginConfiguration _configuration;

    /// <summary>
    /// Initializes a new stub that returns <paramref name="configuration"/> on every call.
    /// </summary>
    public StubPluginConfigurationProvider(PluginConfiguration configuration)
        => _configuration = configuration;

    /// <inheritdoc />
    public PluginConfiguration? GetConfiguration() => _configuration;
}
