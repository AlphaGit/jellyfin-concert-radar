using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Jellyfin.Plugin.ConcertRadar.Tests.Support;

/// <summary>
/// The container Jellyfin hands the plugin: the host services it provides, plus the plugin's own
/// registrations on top. Nothing here is real but the plugin's own types.
/// </summary>
internal static class HostContainer
{
    /// <summary>Builds the container as the Jellyfin 12 host would.</summary>
    public static ServiceProvider AsTheHostWouldBuildIt()
    {
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddSingleton(Substitute.For<IApplicationPaths>());
        services.AddSingleton(Substitute.For<ILibraryManager>());

        new PluginServiceRegistrator().RegisterServices(services, Substitute.For<IServerApplicationHost>());

        return services.BuildServiceProvider();
    }
}
