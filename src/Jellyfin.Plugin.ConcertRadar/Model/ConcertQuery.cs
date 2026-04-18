using System;

namespace Jellyfin.Plugin.ConcertRadar.Model;

/// <summary>Sort field for concert queries.</summary>
public enum ConcertSortField
{
    /// <summary>Sort by event date (default).</summary>
    Date,

    /// <summary>Sort by artist name.</summary>
    Artist,

    /// <summary>Sort by city name.</summary>
    City,
}

/// <summary>
/// Parameters for querying the concerts table.
/// All filter fields are optional — null/empty means "no constraint".
/// </summary>
/// <param name="From">Include events on or after this date (null = no lower bound).</param>
/// <param name="To">Include events on or before this date (null = no upper bound).</param>
/// <param name="Country">Filter by ISO-3166 alpha-2 country code.</param>
/// <param name="City">Case-insensitive city name filter.</param>
/// <param name="Source">Filter by source identifier.</param>
/// <param name="ArtistMbid">Filter by artist MusicBrainz ID.</param>
/// <param name="Sort">Sort field (default <see cref="ConcertSortField.Date"/>).</param>
/// <param name="Page">Zero-based page number (default 0).</param>
/// <param name="PageSize">Maximum items per page (default 50).</param>
public sealed record ConcertQuery(
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    string? Country = null,
    string? City = null,
    string? Source = null,
    string? ArtistMbid = null,
    ConcertSortField Sort = ConcertSortField.Date,
    int Page = 0,
    int PageSize = 50);
