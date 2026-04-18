using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.ConcertRadar.Model;

/// <summary>
/// Unprocessed event record as returned by a source adapter, before normalization.
/// All fields are as close to the source representation as possible.
/// </summary>
/// <param name="SourceEventId">Opaque event identifier assigned by the source.</param>
/// <param name="SourceUrl">Direct URL to the event on the source website (mandatory).</param>
/// <param name="ArtistName">Artist name as returned by the source (mandatory).</param>
/// <param name="EventDateTime">Event start date/time with UTC offset (mandatory).</param>
/// <param name="VenueName">Venue name.</param>
/// <param name="VenueAddress">Venue street address.</param>
/// <param name="City">City name.</param>
/// <param name="Region">Region, state, or province.</param>
/// <param name="Country">ISO-3166 alpha-2 country code.</param>
/// <param name="Lat">Latitude.</param>
/// <param name="Lon">Longitude.</param>
/// <param name="Lineup">Ordered list of performing artists (headliner first).</param>
/// <param name="TicketUrl">Buy-tickets URL (may equal <paramref name="SourceUrl"/>).</param>
/// <param name="PriceMin">Minimum ticket price.</param>
/// <param name="PriceMax">Maximum ticket price.</param>
/// <param name="Currency">Currency code (ISO 4217).</param>
/// <param name="OnSaleAt">On-sale date/time.</param>
/// <param name="IsFestival">Whether this event is classified as a festival.</param>
/// <param name="IsSoldOut">Whether this event is sold out.</param>
public sealed record RawEvent(
    string SourceEventId,
    string SourceUrl,
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
    bool IsFestival,
    bool IsSoldOut);
