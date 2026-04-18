using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ConcertRadar.Model;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ConcertRadar.Storage;

/// <summary>
/// Data-access layer for the <c>artists</c> table.
/// </summary>
public sealed class ArtistRepository
{
    private readonly DatabaseLocator _locator;
    private readonly ILogger<ArtistRepository> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ArtistRepository"/> class.
    /// </summary>
    /// <param name="locator">Database path resolver.</param>
    /// <param name="logger">Logger.</param>
    public ArtistRepository(DatabaseLocator locator, ILogger<ArtistRepository> logger)
    {
        _locator = locator;
        _logger = logger;
    }

    // ── Write ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Inserts new artists from the Jellyfin library or updates <c>name</c>, <c>mbid</c>, and
    /// <c>jellyfin_item_id</c> on existing rows. Does NOT delete artists absent from
    /// <paramref name="items"/> (soft-missing behaviour).
    /// </summary>
    public async Task UpsertFromLibraryAsync(IEnumerable<LibraryArtist> items, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        try
        {
            foreach (var artist in items)
            {
                ct.ThrowIfCancellationRequested();
                string id = ComputeId(artist.Mbid, artist.Name);

                await using var cmd = conn.CreateCommand();
                cmd.Transaction = (SqliteTransaction)tx;
                cmd.CommandText = """
                    INSERT INTO artists (id, mbid, name, jellyfin_item_id, ext_ids)
                    VALUES (@id, @mbid, @name, @jellyfinId, '{}')
                    ON CONFLICT(id) DO UPDATE SET
                        name             = excluded.name,
                        mbid             = COALESCE(excluded.mbid, artists.mbid),
                        jellyfin_item_id = excluded.jellyfin_item_id
                    """;
                cmd.Parameters.AddWithValue("@id", id);
                cmd.Parameters.AddWithValue("@mbid", (object?)artist.Mbid ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@name", artist.Name);
                cmd.Parameters.AddWithValue("@jellyfinId", artist.JellyfinItemId.ToString());
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            throw;
        }

        _logger.LogDebug("UpsertFromLibrary completed.");
    }

    /// <summary>
    /// Updates <c>last_checked_at</c> and error fields after a refresh attempt.
    /// If <paramref name="error"/> is null, <c>consecutive_errors</c> is reset to 0.
    /// Otherwise it is incremented.
    /// </summary>
    public async Task UpdateCheckedAsync(string id, DateTimeOffset now, string? error, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();

        if (error is null)
        {
            cmd.CommandText = """
                UPDATE artists
                SET last_checked_at    = @now,
                    last_error         = NULL,
                    consecutive_errors = 0
                WHERE id = @id
                """;
        }
        else
        {
            cmd.CommandText = """
                UPDATE artists
                SET last_checked_at    = @now,
                    last_error         = @error,
                    consecutive_errors = consecutive_errors + 1
                WHERE id = @id
                """;
            cmd.Parameters.AddWithValue("@error", error);
        }

        cmd.Parameters.AddWithValue("@now", now.ToString("O"));
        cmd.Parameters.AddWithValue("@id", id);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Merges <paramref name="ids"/> into the existing <c>ext_ids</c> JSON object for the artist.
    /// Existing keys are overwritten; keys not present in <paramref name="ids"/> are preserved.
    /// </summary>
    public async Task SetExternalIdsAsync(string id, IReadOnlyDictionary<string, string> ids, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);

        // Read current ext_ids
        string existing = "{}";
        await using (var readCmd = conn.CreateCommand())
        {
            readCmd.CommandText = "SELECT ext_ids FROM artists WHERE id = @id";
            readCmd.Parameters.AddWithValue("@id", id);
            var scalar = await readCmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (scalar is string s) existing = s;
        }

        var merged = JsonSerializer.Deserialize<Dictionary<string, string>>(existing)
            ?? new Dictionary<string, string>();

        foreach (var (k, v) in ids)
            merged[k] = v;

        string newJson = JsonSerializer.Serialize(merged);

        await using var updateCmd = conn.CreateCommand();
        updateCmd.CommandText = "UPDATE artists SET ext_ids = @extIds WHERE id = @id";
        updateCmd.Parameters.AddWithValue("@extIds", newJson);
        updateCmd.Parameters.AddWithValue("@id", id);
        await updateCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Nullifies <c>last_checked_at</c> for all artists, causing all of them to jump
    /// to the front of the round-robin queue on the next scheduler run.
    /// </summary>
    public async Task ResetAllCheckedAsync(CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE artists SET last_checked_at = NULL";
        int rows = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        _logger.LogInformation("ResetAllChecked nullified last_checked_at for {Count} artist(s).", rows);
    }

    // ── Read ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the next batch of artists ordered by <c>last_checked_at ASC NULLS FIRST</c>.
    /// Artists that have never been checked (null) are returned first.
    /// </summary>
    public async Task<IReadOnlyList<StoredArtist>> GetNextBatchAsync(int limit, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, mbid, name, jellyfin_item_id, ext_ids,
                   last_checked_at, last_error, consecutive_errors
            FROM artists
            ORDER BY last_checked_at ASC NULLS FIRST
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@limit", limit);

        var results = new List<StoredArtist>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            results.Add(ReadStoredArtist(reader));

        return results;
    }

    /// <summary>
    /// Returns all artists in the database.
    /// </summary>
    public async Task<IReadOnlyList<StoredArtist>> GetAllAsync(CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, mbid, name, jellyfin_item_id, ext_ids,
                   last_checked_at, last_error, consecutive_errors
            FROM artists
            ORDER BY name ASC
            """;

        var results = new List<StoredArtist>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            results.Add(ReadStoredArtist(reader));

        return results;
    }

    /// <summary>
    /// Returns the number of artists that have never been checked (<c>last_checked_at IS NULL</c>).
    /// </summary>
    public async Task<int> CountUncheckedAsync(CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM artists WHERE last_checked_at IS NULL";
        var scalar = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return Convert.ToInt32(scalar);
    }

    /// <summary>
    /// Resets <c>ext_ids</c> to <c>{}</c> for all artists, forcing re-resolution on the next
    /// scheduler run.
    /// </summary>
    public async Task ClearAllExternalIdsAsync(CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE artists SET ext_ids = '{}'";
        int rows = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        _logger.LogInformation("ClearAllExternalIds reset ext_ids for {Count} artist(s).", rows);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new SqliteConnection(_locator.ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        return conn;
    }

    /// <summary>
    /// Returns the artist's MBID when present; otherwise returns the hex SHA-1 of its
    /// lower-cased name (stable synthetic primary key).
    /// </summary>
    internal static string ComputeId(string? mbid, string name)
    {
        if (!string.IsNullOrWhiteSpace(mbid))
            return mbid;

        byte[] bytes = SHA1.HashData(Encoding.UTF8.GetBytes(name.ToLowerInvariant()));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static StoredArtist ReadStoredArtist(SqliteDataReader r)
    {
        string extIdsJson = r.IsDBNull(4) ? "{}" : r.GetString(4);
        var extIds = JsonSerializer.Deserialize<Dictionary<string, string>>(extIdsJson)
            ?? new Dictionary<string, string>();

        Guid? jellyfinId = null;
        if (!r.IsDBNull(3) && Guid.TryParse(r.GetString(3), out var g))
            jellyfinId = g;

        return new StoredArtist(
            Id:               r.GetString(0),
            Mbid:             r.IsDBNull(1) ? null : r.GetString(1),
            Name:             r.GetString(2),
            JellyfinItemId:   jellyfinId,
            ExternalIds:      extIds,
            LastCheckedAt:    r.IsDBNull(5) ? null : DateTimeOffset.Parse(r.GetString(5)),
            LastError:        r.IsDBNull(6) ? null : r.GetString(6),
            ConsecutiveErrors: r.GetInt32(7));
    }
}
