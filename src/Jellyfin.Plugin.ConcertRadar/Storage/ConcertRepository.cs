using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ConcertRadar.Model;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ConcertRadar.Storage;

/// <summary>
/// Data-access layer for the <c>concerts</c> table.
/// All writes are upserts keyed on <c>(source, source_event_id)</c>.
/// All reads are parameterized — no user input is concatenated into SQL.
/// </summary>
public sealed class ConcertRepository
{
    private readonly DatabaseLocator _locator;
    private readonly ILogger<ConcertRepository> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConcertRepository"/> class.
    /// </summary>
    /// <param name="locator">Database path resolver.</param>
    /// <param name="logger">Logger.</param>
    public ConcertRepository(DatabaseLocator locator, ILogger<ConcertRepository> logger)
    {
        _locator = locator;
        _logger = logger;
    }

    // ── Write ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Inserts a new concert record or updates the existing one identified by
    /// <c>(source, source_event_id)</c>.
    /// </summary>
    public async Task UpsertAsync(ConcertRecord r, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO concerts (
                id, source, source_event_id, source_url,
                artist_mbid, artist_name, event_datetime,
                venue_name, venue_address, city, region, country,
                lat, lon, lineup, ticket_url,
                price_min, price_max, currency, onsale_at,
                fetched_at, last_seen_at)
            VALUES (
                @id, @source, @sourceEventId, @sourceUrl,
                @artistMbid, @artistName, @eventDatetime,
                @venueName, @venueAddress, @city, @region, @country,
                @lat, @lon, @lineup, @ticketUrl,
                @priceMin, @priceMax, @currency, @onsaleAt,
                @fetchedAt, @lastSeenAt)
            ON CONFLICT(source, source_event_id) DO UPDATE SET
                source_url      = excluded.source_url,
                artist_mbid     = excluded.artist_mbid,
                artist_name     = excluded.artist_name,
                event_datetime  = excluded.event_datetime,
                venue_name      = excluded.venue_name,
                venue_address   = excluded.venue_address,
                city            = excluded.city,
                region          = excluded.region,
                country         = excluded.country,
                lat             = excluded.lat,
                lon             = excluded.lon,
                lineup          = excluded.lineup,
                ticket_url      = excluded.ticket_url,
                price_min       = excluded.price_min,
                price_max       = excluded.price_max,
                currency        = excluded.currency,
                onsale_at       = excluded.onsale_at,
                last_seen_at    = excluded.last_seen_at
            """;

        BindConcertRecord(cmd, r);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        _logger.LogDebug("Upserted concert {Id} ({Source}/{EventId})", r.Id, r.Source, r.SourceEventId);
    }

    /// <summary>
    /// Updates <c>last_seen_at</c> for a known concert without changing any other fields.
    /// </summary>
    public async Task MarkSeenAsync(string source, string sourceEventId, DateTimeOffset now, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "UPDATE concerts SET last_seen_at = @now WHERE source = @source AND source_event_id = @eventId";
        cmd.Parameters.AddWithValue("@now", now.ToString("O"));
        cmd.Parameters.AddWithValue("@source", source);
        cmd.Parameters.AddWithValue("@eventId", sourceEventId);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes all concert records whose <c>event_datetime</c> is in the past.
    /// </summary>
    /// <returns>Number of rows deleted.</returns>
    public async Task<int> DeletePastAsync(DateTimeOffset now, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM concerts WHERE event_datetime < @now";
        cmd.Parameters.AddWithValue("@now", now.ToString("O"));
        int rows = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        _logger.LogInformation("DeletePast removed {Count} past concert(s).", rows);
        return rows;
    }

    /// <summary>
    /// Deletes concert records not seen since <paramref name="threshold"/>.
    /// </summary>
    /// <returns>Number of rows deleted.</returns>
    public async Task<int> DeleteStaleAsync(DateTimeOffset threshold, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM concerts WHERE last_seen_at < @threshold";
        cmd.Parameters.AddWithValue("@threshold", threshold.ToString("O"));
        int rows = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        _logger.LogInformation("DeleteStale removed {Count} stale concert(s).", rows);
        return rows;
    }

    /// <summary>
    /// Deletes all concert records from the database.
    /// </summary>
    /// <returns>Number of rows deleted.</returns>
    public async Task<int> PurgeAllAsync(CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM concerts";
        int rows = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        _logger.LogWarning("PurgeAll deleted {Count} concert record(s).", rows);
        return rows;
    }

    // ── Read ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Queries concerts with optional filtering and pagination.
    /// </summary>
    public async Task<QueryResult<ConcertRecord>> QueryAsync(ConcertQuery q, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);

        // Build WHERE clause using a list of conditions and a separate parameter dict.
        var conditions = new List<string>();
        var parms = new Dictionary<string, object?>();

        if (q.From.HasValue)
        {
            conditions.Add("event_datetime >= @from");
            parms["@from"] = q.From.Value.ToString("O");
        }

        if (q.To.HasValue)
        {
            conditions.Add("event_datetime <= @to");
            parms["@to"] = q.To.Value.ToString("O");
        }

        if (!string.IsNullOrWhiteSpace(q.Country))
        {
            conditions.Add("country = @country");
            parms["@country"] = q.Country;
        }

        if (!string.IsNullOrWhiteSpace(q.City))
        {
            conditions.Add("city = @city");
            parms["@city"] = q.City;
        }

        if (!string.IsNullOrWhiteSpace(q.Source))
        {
            conditions.Add("source = @source");
            parms["@source"] = q.Source;
        }

        if (!string.IsNullOrWhiteSpace(q.ArtistMbid))
        {
            conditions.Add("artist_mbid = @artistMbid");
            parms["@artistMbid"] = q.ArtistMbid;
        }

        string where = conditions.Count > 0
            ? "WHERE " + string.Join(" AND ", conditions)
            : string.Empty;

        string orderBy = q.Sort switch
        {
            ConcertSortField.Artist => "ORDER BY artist_name ASC, event_datetime ASC",
            ConcertSortField.City   => "ORDER BY city ASC, event_datetime ASC",
            _                       => "ORDER BY event_datetime ASC",
        };

        // Count query
        int total;
        await using (var countCmd = conn.CreateCommand())
        {
            countCmd.CommandText = $"SELECT COUNT(*) FROM concerts {where}";
            foreach (var (k, v) in parms)
                countCmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
            var scalar = await countCmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            total = Convert.ToInt32(scalar);
        }

        // Data query
        var items = new List<ConcertRecord>();
        await using (var dataCmd = conn.CreateCommand())
        {
            dataCmd.CommandText = $"""
                SELECT id, source, source_event_id, source_url,
                       artist_mbid, artist_name, event_datetime,
                       venue_name, venue_address, city, region, country,
                       lat, lon, lineup, ticket_url,
                       price_min, price_max, currency, onsale_at,
                       fetched_at, last_seen_at
                FROM concerts
                {where}
                {orderBy}
                LIMIT @limit OFFSET @offset
                """;

            foreach (var (k, v) in parms)
                dataCmd.Parameters.AddWithValue(k, v ?? DBNull.Value);

            dataCmd.Parameters.AddWithValue("@limit", q.PageSize);
            dataCmd.Parameters.AddWithValue("@offset", q.Page * q.PageSize);

            await using var reader = await dataCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                items.Add(ReadConcertRecord(reader));
        }

        return new QueryResult<ConcertRecord>(items, total);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new SqliteConnection(_locator.ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        return conn;
    }

    private static void BindConcertRecord(SqliteCommand cmd, ConcertRecord r)
    {
        cmd.Parameters.AddWithValue("@id", r.Id);
        cmd.Parameters.AddWithValue("@source", r.Source);
        cmd.Parameters.AddWithValue("@sourceEventId", r.SourceEventId);
        cmd.Parameters.AddWithValue("@sourceUrl", r.SourceUrl);
        cmd.Parameters.AddWithValue("@artistMbid", (object?)r.ArtistMbid ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@artistName", r.ArtistName);
        cmd.Parameters.AddWithValue("@eventDatetime", r.EventDateTime.ToString("O"));
        cmd.Parameters.AddWithValue("@venueName", (object?)r.VenueName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@venueAddress", (object?)r.VenueAddress ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@city", (object?)r.City ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@region", (object?)r.Region ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@country", (object?)r.Country ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@lat", (object?)r.Lat ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@lon", (object?)r.Lon ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@lineup", JsonSerializer.Serialize(r.Lineup));
        cmd.Parameters.AddWithValue("@ticketUrl", (object?)r.TicketUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@priceMin", r.PriceMin.HasValue ? (object)(double)r.PriceMin.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@priceMax", r.PriceMax.HasValue ? (object)(double)r.PriceMax.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@currency", (object?)r.Currency ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@onsaleAt", r.OnSaleAt.HasValue ? r.OnSaleAt.Value.ToString("O") : DBNull.Value);
        cmd.Parameters.AddWithValue("@fetchedAt", r.FetchedAt.ToString("O"));
        cmd.Parameters.AddWithValue("@lastSeenAt", r.LastSeenAt.ToString("O"));
    }

    private static ConcertRecord ReadConcertRecord(SqliteDataReader r)
    {
        string lineupJson = r.IsDBNull(14) ? "[]" : r.GetString(14);
        var lineup = JsonSerializer.Deserialize<List<string>>(lineupJson) ?? new List<string>();

        return new ConcertRecord(
            Id:            r.GetString(0),
            Source:        r.GetString(1),
            SourceEventId: r.GetString(2),
            SourceUrl:     r.GetString(3),
            ArtistMbid:    r.IsDBNull(4)  ? null : r.GetString(4),
            ArtistName:    r.GetString(5),
            EventDateTime: DateTimeOffset.Parse(r.GetString(6)),
            VenueName:     r.IsDBNull(7)  ? null : r.GetString(7),
            VenueAddress:  r.IsDBNull(8)  ? null : r.GetString(8),
            City:          r.IsDBNull(9)  ? null : r.GetString(9),
            Region:        r.IsDBNull(10) ? null : r.GetString(10),
            Country:       r.IsDBNull(11) ? null : r.GetString(11),
            Lat:           r.IsDBNull(12) ? null : r.GetDouble(12),
            Lon:           r.IsDBNull(13) ? null : r.GetDouble(13),
            Lineup:        lineup,
            TicketUrl:     r.IsDBNull(15) ? null : r.GetString(15),
            PriceMin:      r.IsDBNull(16) ? null : (decimal?)Convert.ToDecimal(r.GetDouble(16)),
            PriceMax:      r.IsDBNull(17) ? null : (decimal?)Convert.ToDecimal(r.GetDouble(17)),
            Currency:      r.IsDBNull(18) ? null : r.GetString(18),
            OnSaleAt:      r.IsDBNull(19) ? null : DateTimeOffset.Parse(r.GetString(19)),
            FetchedAt:     DateTimeOffset.Parse(r.GetString(20)),
            LastSeenAt:    DateTimeOffset.Parse(r.GetString(21)));
    }
}
