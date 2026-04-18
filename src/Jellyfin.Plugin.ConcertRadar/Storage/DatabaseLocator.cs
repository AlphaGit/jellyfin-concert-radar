using System.IO;
using MediaBrowser.Common.Configuration;

namespace Jellyfin.Plugin.ConcertRadar.Storage;

/// <summary>
/// Resolves the path to the plugin's private SQLite database and provides the connection string.
/// Registered as a singleton in DI.
/// </summary>
public sealed class DatabaseLocator
{
    private readonly IApplicationPaths _paths;

    /// <summary>
    /// Initializes a new instance of the <see cref="DatabaseLocator"/> class.
    /// </summary>
    /// <param name="paths">Jellyfin application paths.</param>
    public DatabaseLocator(IApplicationPaths paths)
    {
        _paths = paths;
    }

    /// <summary>
    /// Gets the absolute path to the <c>concertradar/</c> data directory.
    /// </summary>
    public string ConcertRadarDirectory => Path.Combine(_paths.DataPath, "concertradar");

    /// <summary>
    /// Gets the absolute path to the SQLite database file.
    /// </summary>
    public string DatabasePath => Path.Combine(ConcertRadarDirectory, "concerts.db");

    /// <summary>
    /// Gets the SQLite connection string for <see cref="DatabasePath"/>.
    /// </summary>
    public string ConnectionString => $"Data Source={DatabasePath}";

    /// <summary>
    /// Creates <see cref="ConcertRadarDirectory"/> if it does not already exist.
    /// </summary>
    public void EnsureDirectoryExists() => Directory.CreateDirectory(ConcertRadarDirectory);
}
