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
    private readonly IMigrationGate _gate;
    private readonly TimeProvider _clock;
    private readonly ILogger<SourceStateRepository> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SourceStateRepository"/> class.
    /// </summary>
    /// <param name="locator">Database path resolver.</param>
    /// <param name="gate">Migration gate — awaited before first connection open.</param>
    /// <param name="clock">
    /// Time provider — inject <see cref="TimeProvider.System"/> in production;
    /// supply a stub in tests for deterministic midnight-reset logic.
    /// </param>
    /// <param name="logger">Logger.</param>
    public SourceStateRepository(
        DatabaseLocator locator,
        IMigrationGate gate,
        TimeProvider clock,
        ILogger<SourceStateRepository> logger)
    {
        _locator = locator;
        _gate    = gate;
        _clock   = clock;
        _logger  = logger;
    }

    /// <summary>
    /// Convenience constructor for test code that has already applied migrations and does not
    /// need a gate delay.  Uses a pre-signalled <see cref="AlreadyReadyGate"/> internally.
    /// </summary>
    public SourceStateRepository(
        DatabaseLocator locator,
        TimeProvider clock,
        ILogger<SourceStateRepository> logger)
        : this(locator, AlreadyReadyGate.Instance, clock, logger)
    {
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
    /// Single atomic INSERT … ON CONFLICT … DO UPDATE to avoid two round-trips.
    /// </summary>
    public async Task RecordFailureAsync(
        string source,
        string error,
        int threshold,
        TimeSpan cooldown,
        DateTimeOffset now,
        CancellationToken ct)
    {
        string disabledUntil = (now + cooldown).ToString("O");

        await using var conn = await OpenAsync(ct).ConfigureAwait(false);

        // Step 1: ensure the row exists (no-op if already present).
        await using (var ensureCmd = conn.CreateCommand())
        {
            ensureCmd.CommandText = """
                INSERT OR IGNORE INTO source_state (source, status)
                VALUES (@source, 'Ok')
                """;
            ensureCmd.Parameters.AddWithValue("@source", source);
            await ensureCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        // Step 2: atomically increment and conditionally trip the circuit.
        // Single UPDATE avoids a separate SELECT round-trip.
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE source_state
            SET consecutive_errors = consecutive_errors + 1,
                last_error         = @error,
                status             = CASE
                    WHEN consecutive_errors + 1 >= @threshold THEN 'Failing'
                    ELSE status
                END,
                disabled_until     = CASE
                    WHEN consecutive_errors + 1 >= @threshold THEN @disabledUntil
                    ELSE disabled_until
                END
            WHERE source = @source
            RETURNING consecutive_errors
            """;

        cmd.Parameters.AddWithValue("@source", source);
        cmd.Parameters.AddWithValue("@error", error);
        cmd.Parameters.AddWithValue("@threshold", threshold);
        cmd.Parameters.AddWithValue("@disabledUntil", disabledUntil);

        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        int newErrors = result is long l ? (int)l : 1;

        if (newErrors >= threshold)
        {
            _logger.LogWarning(
                "Source {Source} tripped circuit breaker after {Count} consecutive errors. " +
                "Disabling until {Until}.",
                source, newErrors, now + cooldown);
        }
    }

    /// <summary>
    /// Increments the <c>calls_today</c> counter. If the last reset timestamp has crossed
    /// midnight UTC (per the injected <see cref="TimeProvider"/>), the counter is reset to 1
    /// atomically in a single SQL statement.
    /// </summary>
    public async Task IncrementCallCounterAsync(string source, CancellationToken ct)
    {
        DateTimeOffset now = _clock.GetUtcNow();
        // Midnight in UTC as ISO-8601; used in the CASE expression to determine whether
        // calls_today_reset is before today.
        string todayMidnightStr = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero).ToString("O");
        string nowStr = now.ToString("O");

        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();

        // Single statement: upsert + conditional reset + increment.
        // If calls_today_reset < today midnight → reset to 1; otherwise increment.
        cmd.CommandText = """
            INSERT INTO source_state (source, status, calls_today, calls_today_reset)
            VALUES (@source, 'Ok', 1, @now)
            ON CONFLICT(source) DO UPDATE SET
                calls_today       = CASE
                    WHEN source_state.calls_today_reset IS NULL
                      OR source_state.calls_today_reset < @todayMidnight
                    THEN 1
                    ELSE source_state.calls_today + 1
                END,
                calls_today_reset = CASE
                    WHEN source_state.calls_today_reset IS NULL
                      OR source_state.calls_today_reset < @todayMidnight
                    THEN @now
                    ELSE source_state.calls_today_reset
                END
            """;

        cmd.Parameters.AddWithValue("@source", source);
        cmd.Parameters.AddWithValue("@now", nowStr);
        cmd.Parameters.AddWithValue("@todayMidnight", todayMidnightStr);
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
        await _gate.WaitAsync(ct).ConfigureAwait(false);
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
        // Use UtcDateTime.Date (already UTC midnight) wrapped in a zero-offset DateTimeOffset
        // to avoid the local-kind shift that now.Date.ToUniversalTime() would introduce on
        // non-UTC servers.
        DateTimeOffset todayMidnight = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
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
