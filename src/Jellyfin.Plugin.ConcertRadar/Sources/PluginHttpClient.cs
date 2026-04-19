namespace Jellyfin.Plugin.ConcertRadar.Sources;

/// <summary>
/// Named HttpClient constants used by all source adapters and resolvers in this plugin.
/// The named client is registered in <see cref="PluginServiceRegistrator"/> with a
/// 10 MiB <c>MaxResponseContentBufferSize</c> to cap memory consumption on large upstream responses.
/// </summary>
internal static class PluginHttpClient
{
    /// <summary>Name of the plugin-scoped HttpClient registered in DI.</summary>
    public const string ClientName = "concertradar-default";
}
