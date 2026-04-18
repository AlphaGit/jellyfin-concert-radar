using System;
using Jellyfin.Plugin.ConcertRadar.Model;

namespace Jellyfin.Plugin.ConcertRadar.Api.Dtos;

/// <summary>Per-source health status returned by <c>GET /status</c>.</summary>
public sealed record SourceStatusDto
{
    /// <summary>Gets the source identifier.</summary>
    public required string Source { get; init; }

    /// <summary>Gets the operational status.</summary>
    public required string Status { get; init; }

    /// <summary>Gets the number of consecutive failures.</summary>
    public required int ConsecutiveErrors { get; init; }

    /// <summary>Gets when the circuit breaker cooldown expires (null = circuit closed).</summary>
    public DateTimeOffset? DisabledUntil { get; init; }

    /// <summary>Gets the number of calls made today.</summary>
    public required int CallsToday { get; init; }

    /// <summary>Gets the most recent error message.</summary>
    public string? LastError { get; init; }

    /// <summary>Gets when the last successful call was made.</summary>
    public DateTimeOffset? LastSuccessAt { get; init; }

    /// <summary>Maps a <see cref="SourceStateRecord"/> to <see cref="SourceStatusDto"/>.</summary>
    public static SourceStatusDto FromRecord(SourceStateRecord r) => new()
    {
        Source           = r.Source,
        Status           = r.Status.ToString(),
        ConsecutiveErrors = r.ConsecutiveErrors,
        DisabledUntil    = r.DisabledUntil,
        CallsToday       = r.CallsToday,
        LastError        = r.LastError,
        LastSuccessAt    = r.LastSuccessAt,
    };
}
