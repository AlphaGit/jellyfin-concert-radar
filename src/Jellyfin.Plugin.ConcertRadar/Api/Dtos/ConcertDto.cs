using System;
using System.Collections.Generic;
using Jellyfin.Plugin.ConcertRadar.Model;

namespace Jellyfin.Plugin.ConcertRadar.Api.Dtos;

/// <summary>Concert record returned by the API.</summary>
public sealed record ConcertDto
{
    /// <summary>Gets the local UUID primary key.</summary>
    public required string Id { get; init; }

    /// <summary>Gets the source identifier.</summary>
    public required string Source { get; init; }

    /// <summary>Gets the source-assigned event ID.</summary>
    public required string SourceEventId { get; init; }

    /// <summary>Gets the direct URL to the event on the source website.</summary>
    public required string SourceUrl { get; init; }

    /// <summary>Gets the MusicBrainz artist ID (nullable).</summary>
    public string? ArtistMbid { get; init; }

    /// <summary>Gets the artist display name.</summary>
    public required string ArtistName { get; init; }

    /// <summary>Gets the event date/time with UTC offset.</summary>
    public required DateTimeOffset EventDateTime { get; init; }

    /// <summary>Gets the venue name.</summary>
    public string? VenueName { get; init; }

    /// <summary>Gets the venue street address.</summary>
    public string? VenueAddress { get; init; }

    /// <summary>Gets the city.</summary>
    public string? City { get; init; }

    /// <summary>Gets the region / state / province.</summary>
    public string? Region { get; init; }

    /// <summary>Gets the ISO-3166 alpha-2 country code.</summary>
    public string? Country { get; init; }

    /// <summary>Gets the latitude.</summary>
    public double? Lat { get; init; }

    /// <summary>Gets the longitude.</summary>
    public double? Lon { get; init; }

    /// <summary>Gets the ordered artist lineup.</summary>
    public required IReadOnlyList<string> Lineup { get; init; }

    /// <summary>Gets the buy-tickets URL.</summary>
    public string? TicketUrl { get; init; }

    /// <summary>Gets the minimum ticket price.</summary>
    public decimal? PriceMin { get; init; }

    /// <summary>Gets the maximum ticket price.</summary>
    public decimal? PriceMax { get; init; }

    /// <summary>Gets the ISO 4217 currency code.</summary>
    public string? Currency { get; init; }

    /// <summary>Gets the on-sale date/time.</summary>
    public DateTimeOffset? OnSaleAt { get; init; }

    /// <summary>Gets the Jellyfin MusicArtist item GUID when the concert's MBID
    /// resolves to an artist in the local Jellyfin library (nullable).</summary>
    public Guid? JellyfinArtistId { get; init; }

    /// <summary>Maps a <see cref="ConcertRecord"/> to <see cref="ConcertDto"/>.</summary>
    public static ConcertDto FromRecord(ConcertRecord r, Guid? jellyfinArtistId = null) => new()
    {
        Id               = r.Id,
        Source           = r.Source,
        SourceEventId    = r.SourceEventId,
        SourceUrl        = r.SourceUrl,
        ArtistMbid       = r.ArtistMbid,
        ArtistName       = r.ArtistName,
        EventDateTime    = r.EventDateTime,
        VenueName        = r.VenueName,
        VenueAddress     = r.VenueAddress,
        City             = r.City,
        Region           = r.Region,
        Country          = r.Country,
        Lat              = r.Lat,
        Lon              = r.Lon,
        Lineup           = r.Lineup,
        TicketUrl        = r.TicketUrl,
        PriceMin         = r.PriceMin,
        PriceMax         = r.PriceMax,
        Currency         = r.Currency,
        OnSaleAt         = r.OnSaleAt,
        JellyfinArtistId = jellyfinArtistId,
    };
}
