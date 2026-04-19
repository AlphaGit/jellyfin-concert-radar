using Jellyfin.Plugin.ConcertRadar.Library;
using Jellyfin.Plugin.ConcertRadar.RateLimiting;
using Jellyfin.Plugin.ConcertRadar.Resolution;
using Jellyfin.Plugin.ConcertRadar.ScheduledTasks;
using Jellyfin.Plugin.ConcertRadar.Sources;
using Jellyfin.Plugin.ConcertRadar.Storage;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Tasks;
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
        // Migration gate: signals repositories that the schema is ready.
        serviceCollection.AddSingleton<IMigrationGate, MigrationGate>();
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

        // T3.1: Source adapters — registered individually so IEnumerable<ISourceAdapter>
        // resolves all of them. Additional adapters (T5.1, T7.1, etc.) follow the same pattern.
        serviceCollection.AddSingleton<ISourceAdapter, TicketmasterAdapter>();

        // T5.2: Bandsintown adapter.
        serviceCollection.AddSingleton<ISourceAdapter, BandsintownAdapter>();

        // T7.2: EdmTrain adapter.
        serviceCollection.AddSingleton<ISourceAdapter, EdmTrainAdapter>();

        // T8.2: Songkick scraper.
        serviceCollection.AddSingleton<ISourceAdapter, SongkickScrapeAdapter>();

        // T9.2: Dice.fm scraper (ToS opt-in required; IsConfigured guards at fetch time).
        serviceCollection.AddSingleton<ISourceAdapter, DiceScrapeAdapter>();

        // T10.2: Resident Advisor scraper (ToS opt-in required; IsConfigured guards at fetch time).
        // Note: the RA warning banner in admin.html was added in T4.5.
        serviceCollection.AddSingleton<ISourceAdapter, RaScrapeAdapter>();

        // T3.2: Scheduled task — Jellyfin discovers IScheduledTask implementations automatically
        // when they are in the DI container.
        serviceCollection.AddSingleton<IScheduledTask, RefreshConcertsTask>();
    }
}
