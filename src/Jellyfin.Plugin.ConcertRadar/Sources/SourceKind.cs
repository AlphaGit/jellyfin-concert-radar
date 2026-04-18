namespace Jellyfin.Plugin.ConcertRadar.Sources;

/// <summary>Whether the source uses an official API or HTML/JSON scraping.</summary>
public enum SourceKind
{
    /// <summary>Source uses an official API endpoint.</summary>
    Api,

    /// <summary>Source is scraped from HTML or reverse-engineered endpoints.</summary>
    Scrape,
}
