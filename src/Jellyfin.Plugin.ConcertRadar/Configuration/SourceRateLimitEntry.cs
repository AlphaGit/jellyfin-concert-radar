namespace Jellyfin.Plugin.ConcertRadar.Configuration;

/// <summary>
/// A serializable key/value pair mapping a source identifier to a <see cref="RateLimitConfig"/>.
/// Used in place of <c>Dictionary&lt;string, RateLimitConfig&gt;</c> because
/// <see cref="System.Xml.Serialization.XmlSerializer"/> cannot round-trip a generic dictionary.
/// </summary>
public class SourceRateLimitEntry
{
    /// <summary>Gets or sets the source identifier (e.g. "ticketmaster").</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>Gets or sets the rate-limit configuration for this source.</summary>
    public RateLimitConfig Config { get; set; } = new();
}
