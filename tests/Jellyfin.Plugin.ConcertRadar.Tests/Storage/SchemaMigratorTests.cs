using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Jellyfin.Plugin.ConcertRadar.Tests.Support;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Jellyfin.Plugin.ConcertRadar.Tests.Storage;

/// <summary>
/// Integration tests for <see cref="Jellyfin.Plugin.ConcertRadar.Storage.SchemaMigrator"/>.
/// Each test spins up a fresh temp-file database.
/// </summary>
public class SchemaMigratorTests
{
    [Fact]
    public async Task Migrator_AppliesFirstMigration_OnEmptyDb()
    {
        await using var db = await TestDatabase.CreateAsync();

        // All tables defined in SPEC §5 must exist.
        await using var conn = new SqliteConnection(db.Locator.ConnectionString);
        await conn.OpenAsync();

        var tables = await GetTableNamesAsync(conn);
        tables.Should().Contain("concerts",       because: "migration 001 creates the concerts table");
        tables.Should().Contain("artists",         because: "migration 001 creates the artists table");
        tables.Should().Contain("source_state",    because: "migration 001 creates the source_state table");
        tables.Should().Contain("schema_version",  because: "schema_version must always exist");

        // Key indexes must exist.
        var indexes = await GetIndexNamesAsync(conn);
        indexes.Should().Contain("ix_concerts_datetime");
        indexes.Should().Contain("ix_concerts_artist_date");
        indexes.Should().Contain("ix_concerts_geo_date");
        indexes.Should().Contain("ix_artists_last_checked");
    }

    [Fact]
    public async Task Migrator_IsIdempotent()
    {
        await using var db = await TestDatabase.CreateAsync();

        // Run migration a second time — should be a no-op with no exceptions.
        var migrator = new Jellyfin.Plugin.ConcertRadar.Storage.SchemaMigrator(
            db.Locator,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Jellyfin.Plugin.ConcertRadar.Storage.SchemaMigrator>.Instance);

        var act = async () => await migrator.MigrateAsync(CancellationToken.None);
        await act.Should().NotThrowAsync();

        // schema_version should have exactly one row (version 1).
        await using var conn = new SqliteConnection(db.Locator.ConnectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM schema_version";
        var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        count.Should().Be(1, because: "second run must not insert duplicate schema_version rows");
    }

    [Fact]
    public async Task Migrator_TracksVersionInSchemaVersionTable()
    {
        await using var db = await TestDatabase.CreateAsync();

        await using var conn = new SqliteConnection(db.Locator.ConnectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT version, applied_at FROM schema_version WHERE version = 1";
        await using var reader = await cmd.ExecuteReaderAsync();

        var hasRow = await reader.ReadAsync();
        hasRow.Should().BeTrue(because: "migration 001 must be recorded in schema_version");

        int version = reader.GetInt32(0);
        string appliedAt = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);

        version.Should().Be(1);
        appliedAt.Should().NotBeNullOrWhiteSpace(because: "applied_at must be populated by the migrator");

        // Verify it's parseable as a DateTimeOffset.
        DateTimeOffset.TryParse(appliedAt, out _).Should().BeTrue(
            because: "applied_at must be a valid ISO 8601 timestamp");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task<System.Collections.Generic.List<string>> GetTableNamesAsync(SqliteConnection conn)
    {
        var names = new System.Collections.Generic.List<string>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table'";
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            names.Add(reader.GetString(0));
        return names;
    }

    private static async Task<System.Collections.Generic.List<string>> GetIndexNamesAsync(SqliteConnection conn)
    {
        var names = new System.Collections.Generic.List<string>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='index'";
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            names.Add(reader.GetString(0));
        return names;
    }
}
