using System;

namespace Jellyfin.Plugin.ConcertRadar.Sources;

/// <summary>
/// Shared constants used across all source adapters.  Centralised here to avoid
/// duplicating magic literals in each adapter class.
/// </summary>
internal static class AdapterDefaults
{
    /// <summary>
    /// Back-off delays between successive retry attempts (200 ms, 800 ms, 3.2 s).
    /// Array length equals the number of waits; total attempts = length + 1.
    /// </summary>
    public static readonly TimeSpan[] RetryBackoffs =
    [
        TimeSpan.FromMilliseconds(200),
        TimeSpan.FromMilliseconds(800),
        TimeSpan.FromMilliseconds(3200),
    ];

    /// <summary>Total number of retry attempts (initial + retries = 4).</summary>
    public const int MaxRetryAttempts = 3;

    /// <summary>Maximum number of paginated pages to fetch per adapter call.</summary>
    public const int MaxPaginationPages = 10;

    /// <summary>Default page size for paginated API requests.</summary>
    public const int DefaultPageSize = 200;

    /// <summary>
    /// Default <c>Retry-After</c> fallback when the header is missing
    /// (used when computing the backoff floor from a 429 response).
    /// </summary>
    public static readonly TimeSpan DefaultRetryAfterFallback = TimeSpan.FromSeconds(60);
}
