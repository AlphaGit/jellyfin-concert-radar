namespace Jellyfin.Plugin.ConcertRadar.Configuration;

/// <summary>
/// Per-source rate-limit override.
/// </summary>
public class RateLimitConfig
{
    /// <summary>Gets or sets the maximum requests per second for this source.</summary>
    public double RequestsPerSecond { get; set; }

    /// <summary>Gets or sets the maximum requests per day for this source (null = unlimited).</summary>
    public int? RequestsPerDay { get; set; }
}
