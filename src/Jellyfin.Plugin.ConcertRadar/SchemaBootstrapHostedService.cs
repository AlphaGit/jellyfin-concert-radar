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
/// Signals <see cref="IMigrationGate"/> on completion so repositories do not serve
/// requests before the schema exists.
/// </summary>
public sealed class SchemaBootstrapHostedService : IHostedService
{
    private readonly SchemaMigrator _migrator;
    private readonly IMigrationGate _gate;
    private readonly ILogger<SchemaBootstrapHostedService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SchemaBootstrapHostedService"/> class.
    /// </summary>
    /// <param name="migrator">Schema migrator.</param>
    /// <param name="gate">Migration gate to signal when migration completes.</param>
    /// <param name="logger">Logger.</param>
    public SchemaBootstrapHostedService(
        SchemaMigrator migrator,
        IMigrationGate gate,
        ILogger<SchemaBootstrapHostedService> logger)
    {
        _migrator = migrator;
        _gate     = gate;
        _logger   = logger;
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("ConcertRadar: running schema migration.");
        await _migrator.MigrateAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("ConcertRadar: schema migration complete.");

        var plugin = Plugin.Instance;
        if (plugin is not null)
        {
            if (PluginConfigurationDefaults.SeedIfEmpty(plugin.Configuration))
            {
                _logger.LogInformation("ConcertRadar: seeded default configuration entries.");
                plugin.SaveConfiguration();
            }
        }

        // Signal all waiting repositories that the schema is ready.
        _gate.SignalReady();
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
