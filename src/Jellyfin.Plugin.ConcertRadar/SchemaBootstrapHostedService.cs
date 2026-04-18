using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ConcertRadar.Configuration;
using Jellyfin.Plugin.ConcertRadar.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ConcertRadar;

/// <summary>
/// An <see cref="IHostedService"/> that runs the SQLite schema migration and
/// seeds default configuration entries once at startup. Registered via
/// <c>AddHostedService</c>; the host calls <see cref="StartAsync"/> during startup.
/// </summary>
public sealed class SchemaBootstrapHostedService : IHostedService
{
    private readonly SchemaMigrator _migrator;
    private readonly ILogger<SchemaBootstrapHostedService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SchemaBootstrapHostedService"/> class.
    /// </summary>
    /// <param name="migrator">Schema migrator.</param>
    /// <param name="logger">Logger.</param>
    public SchemaBootstrapHostedService(SchemaMigrator migrator, ILogger<SchemaBootstrapHostedService> logger)
    {
        _migrator = migrator;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("ConcertRadar: running schema migration.");
        await _migrator.MigrateAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("ConcertRadar: schema migration complete.");

        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return;
        }

        if (PluginConfigurationDefaults.SeedIfEmpty(plugin.Configuration))
        {
            _logger.LogInformation("ConcertRadar: seeded default configuration entries.");
            plugin.SaveConfiguration();
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
