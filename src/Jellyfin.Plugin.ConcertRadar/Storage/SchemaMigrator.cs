using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ConcertRadar.Storage;

/// <summary>
/// Applies ordered SQL migrations from embedded resources to the plugin's private SQLite database.
/// Running <see cref="MigrateAsync"/> multiple times is safe — already-applied versions are skipped.
/// </summary>
public sealed partial class SchemaMigrator
{
    private static readonly Assembly _assembly = typeof(SchemaMigrator).Assembly;

    // Matches embedded resource names like "...Storage.Migrations.001_initial.sql"
    [GeneratedRegex(@"\.(\d{3}_[^.]+)\.sql$", RegexOptions.IgnoreCase)]
    private static partial Regex MigrationResourcePattern();

    private readonly DatabaseLocator _locator;
    private readonly ILogger<SchemaMigrator> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SchemaMigrator"/> class.
    /// </summary>
    /// <param name="locator">Database path resolver.</param>
    /// <param name="logger">Logger.</param>
    public SchemaMigrator(DatabaseLocator locator, ILogger<SchemaMigrator> logger)
    {
        _locator = locator;
        _logger = logger;
    }

    /// <summary>
    /// Ensures the database directory exists and applies any pending SQL migrations.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task MigrateAsync(CancellationToken ct)
    {
        _locator.EnsureDirectoryExists();

        await using var conn = new SqliteConnection(_locator.ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        // Ensure schema_version exists before querying it (chicken-and-egg guard).
        await EnsureSchemaVersionTableAsync(conn, ct).ConfigureAwait(false);

        int currentVersion = await GetCurrentVersionAsync(conn, ct).ConfigureAwait(false);
        _logger.LogInformation("ConcertRadar DB at schema version {Version}", currentVersion);

        var pending = GetPendingMigrations(currentVersion);
        if (pending.Count == 0)
        {
            _logger.LogDebug("No pending migrations.");
            return;
        }

        foreach (var (version, resourceName) in pending)
        {
            ct.ThrowIfCancellationRequested();
            _logger.LogInformation("Applying migration {Version} ({Resource})", version, resourceName);

            string sql = ReadEmbeddedResource(resourceName);

            await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
            try
            {
                await using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = (SqliteTransaction)tx;
                    cmd.CommandText = sql;
                    await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                await using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = (SqliteTransaction)tx;
                    cmd.CommandText = "INSERT OR IGNORE INTO schema_version (version, applied_at) VALUES (@v, @ts)";
                    cmd.Parameters.AddWithValue("@v", version);
                    cmd.Parameters.AddWithValue("@ts", DateTimeOffset.UtcNow.ToString("O"));
                    await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                await tx.CommitAsync(ct).ConfigureAwait(false);
                _logger.LogInformation("Migration {Version} applied successfully.", version);
            }
            catch
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                throw;
            }
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task EnsureSchemaVersionTableAsync(SqliteConnection conn, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "CREATE TABLE IF NOT EXISTS schema_version (version INTEGER PRIMARY KEY, applied_at TEXT NOT NULL)";
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<int> GetCurrentVersionAsync(SqliteConnection conn, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_version";
        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return Convert.ToInt32(result);
    }

    private static List<(int Version, string ResourceName)> GetPendingMigrations(int currentVersion)
    {
        var regex = MigrationResourcePattern();
        var prefix = "Jellyfin.Plugin.ConcertRadar.Storage.Migrations.";

        return _assembly
            .GetManifestResourceNames()
            .Where(n => n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && n.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .Select(n =>
            {
                var m = regex.Match(n);
                if (!m.Success) return ((int Version, string ResourceName)?)null;
                int version = int.Parse(m.Groups[1].Value[..3]);
                return (version, n);
            })
            .Where(x => x.HasValue && x!.Value.Version > currentVersion)
            .Select(x => x!.Value)
            .OrderBy(x => x.Version)
            .ToList();
    }

    private static string ReadEmbeddedResource(string resourceName)
    {
        using var stream = _assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource not found: {resourceName}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
