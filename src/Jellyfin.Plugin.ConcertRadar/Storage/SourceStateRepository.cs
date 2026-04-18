using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ConcertRadar.Model;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ConcertRadar.Storage;

/// <summary>
/// Data-access layer for the <c>source_state</c> table.
/// Manages circuit-breaker state and daily call counters.
/// </summary>
public sealed class SourceStateRepository
{
    private readonly DatabaseLocator _locator;
    private readonly TimeProvider _clock;
    private readonly ILogger<SourceStateRepository> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SourceStateRepository"/> class.
    /// </summary>
    /// <param name="locator">Database path resolver.</param>
    /// <param name="clock">
    /// Time provider — inject <see cref="TimeProvider.System"/> in production;
    /// supply a stub in tests for deterministic midnight-reset logic.
    /// </param>
    /// <param name="logger">Logger.</param>
    public SourceStateRepository(
        DatabaseLocator locator,
        TimeProvider clock,
        ILogger<SourceStateRepository> logger)
    {
        _locator = locator;
        _clock = clock;
        _logger = logger;
    }

    // ── Read ──────────────────────────────────────────────────────────────────

    /// <summary>Returns all source state rows.</summary>
    public async Task<IReadOnlyList<SourceStateRecord>> GetAllAsync(CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT source, status, consecutive_errors, disabled_until,
                   calls_today, calls_today_reset, next_allowed_at,
                   last_error, last_success_at
            FROM source_state
            """;

        var results = new List<SourceStateRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            results.Add(ReadRecord(reader));

        return results;
    }

    /// <summary>Returns the state row for a single source, or null if not found.</summary>
    public async Task<SourceStateRecord?> GetAsync(string source, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT source, status, consecutive_errors, disabled_until,
                   calls_today, calls_today_reset, next_allowed_at,
                   last_error, last_success_at
            FROM source_state
            WHERE source = @source
            """;
        cmd.Parameters.AddWithValue("@source", source);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (await reader.ReadAsync(ct).ConfigureAwait(false))
            return ReadRecord(reader);

        return null;
    }

    /// <summary>
    /// Returns <c>true</c> if <c>disabled_until</c> is set and is still in the future,
    /// meaning the circuit breaker is open and the source should be skipped.
    /// </summary>
    public async Task<bool> IsOpenAsync(string source, DateTimeOffset now, CancellationToken ct)
    {
        var state = await GetAsync(source, ct).ConfigureAwait(false);
        if (state is null) return false;
        return state.DisabledUntil.HasValue && state.DisabledUntil.Value > now;
    }

    /// <summary>
    /// Returns how many calls can still be made today for the given source
    /// against <paramref name="dailyBudget"/>.
    /// </summary>
    public async Task<int> GetAllowanceAsync(string source, int dailyBudget, CancellationToken ct)
    {
        var state = await GetAsync(source, ct).ConfigureAwait(false);
        if (state is null) return dailyBudget;

        // If calls_today_reset crossed midnight, the counter is stale — treat as 0 spent.
        if (HasCrossedMidnight(state.CallsTodayReset))
            return dailyBudget;

        int remaining = dailyBudget - state.CallsToday;
        return remaining < 0 ? 0 : remaining;
    }

    // ── Write ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Records a successful adapter call: clears error counter, sets <c>last_success_at</c>,
    /// and transitions status to <see cref="SourceStatus.Ok"/> (unless <see cref="SourceStatus.Disabled"/>).
    /// </summary>
    public async Task RecordSuccessAsync(string source, DateTimeOffset now, CancellationToken ct)
    {
        await EnsureRowAsync(source, ct).ConfigureAwait(false);

        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE source_state
            SET consecutive_errors = 0,
                last_success_at    = @now,
                last_error         = NULL,
                disabled_until     = NULL,
                status             = CASE
                    WHEN status = 'Disabled' THEN 'Disabled'
                    ELSE 'Ok'
                END
            WHERE source = @source
            """;
        cmd.Parameters.AddWithValue("@now", now.ToString("O"));
        cmd.Parameters.AddWithValue("@source", source);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Records an adapter failure. If <c>consecutive_errors</c> reaches
    /// <paramref name="threshold"/>, the circuit trips: status becomes
    /// <see cref="SourceStatus.Failing"/> and <c>disabled_until</c> is set.
    /// </summary>
    public async Task RecordFailureAsync(
        string source,
        string error,
        int threshold,
        TimeSpan cooldown,
        DateTimeOffset now,
        CancellationToken ct)
    {
        await EnsureRowAsync(source, ct).ConfigureAwait(false);

        var state = await GetAsync(source, ct).ConfigureAwait(false);
        int newErrors = (state?.ConsecutiveErrors ?? 0) + 1;
        bool tripCircuit = newErrors >= threshold;

        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();

        if (tripCircuit)
        {
            _logger.LogWarning(
                "Source {Source} tripped circuit breaker after {Count} consecutive errors. " +
                "Disabling until {Until}.",
                source, newErrors, now + cooldown);

            cmd.CommandText = """
                UPDATE source_state
                SET consecutive_errors = @errors,
                    last_error         = @error,
                    status             = 'Failing',
                    disabled_until     = @disabledUntil
                WHERE source = @source
                """;
            cmd.Parameters.AddWithValue("@disabledUntil", (now + cooldown).ToString("O"));
        }
        else
        {
            cmd.CommandText = """
                UPDATE source_state
                SET consecutive_errors = @errors,
                    last_error         = @error
                WHERE source = @source
                """;
        }

        cmd.Parameters.AddWithValue("@errors", newErrors);
        cmd.Parameters.AddWithValue("@error", error);
        cmd.Parameters.AddWithValue("@source", source);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Increments the <c>calls_today</c> counter. If the last reset timestamp has crossed
    /// midnight UTC (per the injected <see cref="TimeProvider"/>), the counter is reset to 1
    /// before incrementing.
    /// </summary>
    public async Task IncrementCallCounterAsync(string source, CancellationToken ct)
    {
        await EnsureRowAsync(source, ct).ConfigureAwait(false);

        var state = await GetAsync(source, ct).ConfigureAwait(false);
        DateTimeOffset now = _clock.GetUtcNow();

        bool reset = HasCrossedMidnight(state?.CallsTodayReset);

        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();

        if (reset)
        {
            cmd.CommandText = """
                UPDATE source_state
                SET calls_today       = 1,
                    calls_today_reset = @now
                WHERE source = @source
                """;
        }
        else
        {
            cmd.CommandText = """
                UPDATE source_state
                SET calls_today = calls_today + 1
                WHERE source = @source
                """;
        }

        cmd.Parameters.AddWithValue("@now", now.ToString("O"));
        cmd.Parameters.AddWithValue("@source", source);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Clears the circuit breaker, error counters, and call budget for a source.
    /// Transitions status to <see cref="SourceStatus.Ok"/>.
    /// </summary>
    public async Task ResetAsync(string source, CancellationToken ct)
    {
        await EnsureRowAsync(source, ct).ConfigureAwait(false);

        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE source_state
            SET consecutive_errors = 0,
                disabled_until     = NULL,
                calls_today        = 0,
                calls_today_reset  = NULL,
                next_allowed_at    = NULL,
                last_error         = NULL,
                status             = 'Ok'
            WHERE source = @source
            """;
        cmd.Parameters.AddWithValue("@source", source);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        _logger.LogInformation("Source {Source} circuit-breaker state has been reset.", source);
    }

    /// <summary>
    /// Persists a rate-limiter backoff floor to <c>source_state.next_allowed_at</c> so it
    /// survives a plugin restart.  Pass <c>null</c> to clear the floor.
    /// </summary>
    public async Task SetNextAllowedAtAsync(string source, DateTimeOffset? until, CancellationToken ct)
    {
        await EnsureRowAsync(source, ct).ConfigureAwait(false);

        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE source_state
            SET next_allowed_at = @val
            WHERE source = @source
            """;
        cmd.Parameters.AddWithValue("@val",
            until.HasValue ? (object)until.Value.ToString("O") : DBNull.Value);
        cmd.Parameters.AddWithValue("@source", source);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates or updates the status for a source (used for initialization and manual overrides).
    /// </summary>
    public async Task UpsertStatusAsync(string source, SourceStatus status, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO source_state (source, status)
            VALUES (@source, @status)
            ON CONFLICT(source) DO UPDATE SET status = excluded.status
            """;
        cmd.Parameters.AddWithValue("@source", source);
        cmd.Parameters.AddWithValue("@status", status.ToString());
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new SqliteConnection(_locator.ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        return conn;
    }

    /// <summary>
    /// Inserts a default row for <paramref name="source"/> if one does not already exist.
    /// </summary>
    private async Task EnsureRowAsync(string source, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO source_state (source, status)
            VALUES (@source, 'Ok')
            """;
        cmd.Parameters.AddWithValue("@source", source);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="lastReset"/> is null or was before today's
    /// midnight UTC (per the injected clock), indicating the daily counter should be reset.
    /// </summary>
    private bool HasCrossedMidnight(DateTimeOffset? lastReset)
    {
        if (!lastReset.HasValue) return true;
        DateTimeOffset now = _clock.GetUtcNow();
        DateTimeOffset todayMidnight = now.Date.ToUniversalTime();
        return lastReset.Value < todayMidnight;
    }

    private static SourceStateRecord ReadRecord(SqliteDataReader r)
    {
        var status = Enum.Parse<SourceStatus>(r.GetString(1), ignoreCase: true);
        return new SourceStateRecord(
            Source:           r.GetString(0),
            Status:           status,
            ConsecutiveErrors: r.GetInt32(2),
            DisabledUntil:    r.IsDBNull(3)  ? null : DateTimeOffset.Parse(r.GetString(3)),
            CallsToday:       r.GetInt32(4),
            CallsTodayReset:  r.IsDBNull(5)  ? null : DateTimeOffset.Parse(r.GetString(5)),
            NextAllowedAt:    r.IsDBNull(6)  ? null : DateTimeOffset.Parse(r.GetString(6)),
            LastError:        r.IsDBNull(7)  ? null : r.GetString(7),
            LastSuccessAt:    r.IsDBNull(8)  ? null : DateTimeOffset.Parse(r.GetString(8)));
    }
}
