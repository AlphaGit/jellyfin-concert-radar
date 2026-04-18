using Jellyfin.Plugin.ConcertRadar.Library;
using Jellyfin.Plugin.ConcertRadar.RateLimiting;
using Jellyfin.Plugin.ConcertRadar.Resolution;
using Jellyfin.Plugin.ConcertRadar.Storage;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Jellyfin.Plugin.ConcertRadar;

/// <summary>
/// Registers all plugin services into the Jellyfin DI container.
/// Jellyfin discovers this class by scanning plugin assemblies for <see cref="IPluginServiceRegistrator"/> implementations.
/// </summary>
public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // Core storage layer — singletons because they hold no per-request state.
        serviceCollection.AddSingleton<DatabaseLocator>();
        serviceCollection.AddSingleton<SchemaMigrator>();
        serviceCollection.AddSingleton<ConcertRepository>();
        serviceCollection.AddSingleton<ArtistRepository>();
        serviceCollection.AddSingleton<SourceStateRepository>();

        // Provide the system TimeProvider so SourceStateRepository can be tested with a
        // stub clock. Only register if not already present (allows test host to override).
        serviceCollection.TryAddSingleton(TimeProvider.System);

        // Run schema migration exactly once at startup before any repository is used.
        serviceCollection.AddHostedService<SchemaBootstrapHostedService>();

        // T2.1: Rate limiter infrastructure.
        serviceCollection.TryAddSingleton<IPluginConfigurationProvider, DefaultPluginConfigurationProvider>();
        serviceCollection.AddSingleton<HostRateLimiter>();

        // T2.2: Library artist enumerator (depends on ILibraryManager from Jellyfin's DI).
        serviceCollection.AddSingleton<LibraryArtistEnumerator>();

        // T2.3: MusicBrainz resolver.
        serviceCollection.AddSingleton<MusicBrainzResolver>();

        // ISourceAdapter registrations are added in Phase 3+ (T3.1, T5.1, T7.1, etc.).
        // When adapters are registered they should be added as:
        //   serviceCollection.AddSingleton<ISourceAdapter, TicketmasterAdapter>();
        // The scheduled task resolves IEnumerable<ISourceAdapter> from the container.
    }
}
