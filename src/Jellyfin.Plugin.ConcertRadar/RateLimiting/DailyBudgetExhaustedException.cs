using System;

namespace Jellyfin.Plugin.ConcertRadar.RateLimiting;

/// <summary>
/// Thrown by <see cref="HostRateLimiter.AcquireAsync"/> when a source's daily API call
/// budget has been exhausted and no more requests may be made until midnight UTC.
/// </summary>
public sealed class DailyBudgetExhaustedException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DailyBudgetExhaustedException"/> class.
    /// </summary>
    /// <param name="sourceId">The source whose budget was exhausted.</param>
    public DailyBudgetExhaustedException(string sourceId)
        : base($"Daily API budget exhausted for source '{sourceId}'.")
    {
        SourceId = sourceId;
    }

    /// <summary>Gets the source identifier whose budget was exhausted.</summary>
    public string SourceId { get; }
}
