using System;

namespace Jellyfin.Plugin.ConcertRadar.Model;

/// <summary>
/// Maps a row in the <c>source_state</c> table.
/// </summary>
/// <param name="Source">Source identifier primary key (e.g. "ticketmaster").</param>
/// <param name="Status">Current operational status.</param>
/// <param name="ConsecutiveErrors">Number of consecutive refresh failures.</param>
/// <param name="DisabledUntil">When the circuit breaker cooldown expires (null = circuit closed).</param>
/// <param name="CallsToday">Number of API calls made today.</param>
/// <param name="CallsTodayReset">The UTC timestamp of the last midnight reset.</param>
/// <param name="NextAllowedAt">Earliest time a new request is allowed (from Retry-After header).</param>
/// <param name="LastError">Most recent error message (null = none).</param>
/// <param name="LastSuccessAt">Timestamp of the last successful call (null = never).</param>
public sealed record SourceStateRecord(
    string Source,
    SourceStatus Status,
    int ConsecutiveErrors,
    DateTimeOffset? DisabledUntil,
    int CallsToday,
    DateTimeOffset? CallsTodayReset,
    DateTimeOffset? NextAllowedAt,
    string? LastError,
    DateTimeOffset? LastSuccessAt);
