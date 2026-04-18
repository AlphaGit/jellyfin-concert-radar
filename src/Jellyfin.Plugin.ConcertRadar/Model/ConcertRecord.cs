using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.ConcertRadar.Model;

/// <summary>
/// Represents a single normalized concert record stored in the <c>concerts</c> table.
/// </summary>
/// <param name="Id">Local UUID primary key.</param>
/// <param name="Source">Source identifier (e.g. "ticketmaster").</param>
/// <param name="SourceEventId">Opaque event ID assigned by the source.</param>
/// <param name="SourceUrl">Direct URL back to the source event page (mandatory).</param>
/// <param name="ArtistMbid">MusicBrainz artist ID (nullable).</param>
/// <param name="ArtistName">Canonical artist name.</param>
/// <param name="EventDateTime">Date/time of the event with UTC offset.</param>
/// <param name="VenueName">Venue name.</param>
/// <param name="VenueAddress">Venue street address.</param>
/// <param name="City">City.</param>
/// <param name="Region">Region / state / province.</param>
/// <param name="Country">ISO-3166 alpha-2 country code.</param>
/// <param name="Lat">Latitude.</param>
/// <param name="Lon">Longitude.</param>
/// <param name="Lineup">Artist lineup (headliner + supports).</param>
/// <param name="TicketUrl">Buy-tickets URL (may equal <paramref name="SourceUrl"/>).</param>
/// <param name="PriceMin">Minimum ticket price.</param>
/// <param name="PriceMax">Maximum ticket price.</param>
/// <param name="Currency">Currency code (ISO 4217).</param>
/// <param name="OnSaleAt">On-sale date/time.</param>
/// <param name="FetchedAt">When this record was first fetched.</param>
/// <param name="LastSeenAt">When this record was last confirmed present in a source response.</param>
public sealed record ConcertRecord(
    string Id,
    string Source,
    string SourceEventId,
    string SourceUrl,
    string? ArtistMbid,
    string ArtistName,
    DateTimeOffset EventDateTime,
    string? VenueName,
    string? VenueAddress,
    string? City,
    string? Region,
    string? Country,
    double? Lat,
    double? Lon,
    IReadOnlyList<string> Lineup,
    string? TicketUrl,
    decimal? PriceMin,
    decimal? PriceMax,
    string? Currency,
    DateTimeOffset? OnSaleAt,
    DateTimeOffset FetchedAt,
    DateTimeOffset LastSeenAt);
