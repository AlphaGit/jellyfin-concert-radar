namespace Jellyfin.Plugin.ConcertRadar.Configuration;

/// <summary>
/// Defines a geographic area to filter concerts by.
/// </summary>
public class LocationFilter
{
    /// <summary>Gets or sets the city name.</summary>
    public string City { get; set; } = string.Empty;

    /// <summary>Gets or sets the region / state / province.</summary>
    public string Region { get; set; } = string.Empty;

    /// <summary>Gets or sets the ISO-3166 alpha-2 country code.</summary>
    public string Country { get; set; } = string.Empty;

    /// <summary>Gets or sets the latitude for radius-based filtering.</summary>
    public double? Lat { get; set; }

    /// <summary>Gets or sets the longitude for radius-based filtering.</summary>
    public double? Lon { get; set; }

    /// <summary>Gets or sets the search radius in kilometres (default 80).</summary>
    public int RadiusKm { get; set; } = 80;
}
