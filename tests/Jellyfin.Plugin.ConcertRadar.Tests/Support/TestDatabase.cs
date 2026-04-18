using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ConcertRadar.Storage;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Jellyfin.Plugin.ConcertRadar.Tests.Support;

/// <summary>
/// Creates a real file-backed SQLite database in a temp directory, applies migrations,
/// and exposes wired repository instances. Disposal deletes the temp directory.
/// </summary>
internal sealed class TestDatabase : IAsyncDisposable
{
    private readonly string _tempDir;
    private bool _disposed;

    private TestDatabase(
        string tempDir,
        DatabaseLocator locator,
        TimeProvider clock)
    {
        _tempDir = tempDir;
        Locator = locator;
        Concerts = new ConcertRepository(locator, NullLogger<ConcertRepository>.Instance);
        Artists = new ArtistRepository(locator, NullLogger<ArtistRepository>.Instance);
        SourceState = new SourceStateRepository(locator, clock, NullLogger<SourceStateRepository>.Instance);
    }

    public DatabaseLocator Locator { get; }
    public ConcertRepository Concerts { get; }
    public ArtistRepository Artists { get; }
    public SourceStateRepository SourceState { get; }

    /// <summary>
    /// Creates a temp-file database, applies all migrations, and returns the wired instance.
    /// Uses <see cref="TimeProvider.System"/> for the source-state repository.
    /// </summary>
    public static Task<TestDatabase> CreateAsync(CancellationToken ct = default)
        => CreateAsync(TimeProvider.System, ct);

    /// <summary>
    /// Creates a temp-file database with a custom clock (for circuit-breaker / midnight tests).
    /// </summary>
    public static async Task<TestDatabase> CreateAsync(TimeProvider clock, CancellationToken ct = default)
    {
        // Each test gets a unique temp directory so tests never share state.
        string tempDir = Path.Combine(Path.GetTempPath(), "cr_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        // DatabaseLocator derives paths as: DataPath/concertradar/concerts.db
        // We want DataPath == tempDir so the DB lands at tempDir/concertradar/concerts.db.
        var paths = Substitute.For<IApplicationPaths>();
        paths.DataPath.Returns(tempDir);

        var locator = new DatabaseLocator(paths);
        var migrator = new SchemaMigrator(locator, NullLogger<SchemaMigrator>.Instance);
        await migrator.MigrateAsync(ct).ConfigureAwait(false);

        return new TestDatabase(tempDir, locator, clock);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await Task.Run(() =>
        {
            try { Directory.Delete(_tempDir, recursive: true); }
            catch { /* best-effort cleanup */ }
        }).ConfigureAwait(false);
    }
}
