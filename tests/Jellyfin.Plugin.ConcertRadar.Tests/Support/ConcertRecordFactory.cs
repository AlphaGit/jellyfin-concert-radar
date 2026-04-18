using System;
using System.Collections.Generic;
using Jellyfin.Plugin.ConcertRadar.Model;

namespace Jellyfin.Plugin.ConcertRadar.Tests.Support;

/// <summary>
/// Factory helpers for building <see cref="ConcertRecord"/> instances in tests.
/// </summary>
internal static class ConcertRecordFactory
{
    private static readonly DateTimeOffset BaseTime =
        new DateTimeOffset(2025, 6, 15, 20, 0, 0, TimeSpan.Zero);

    public static ConcertRecord Create(
        string? id = null,
        string source = "ticketmaster",
        string sourceEventId = "evt-001",
        string sourceUrl = "https://ticketmaster.com/event/evt-001",
        string? artistMbid = "a74b1b7f-71a5-4011-9441-d0b5e4122711",
        string artistName = "Radiohead",
        DateTimeOffset? eventDateTime = null,
        string? venueName = "Madison Square Garden",
        string? city = "New York",
        string? region = "NY",
        string? country = "US",
        double? lat = 40.7505,
        double? lon = -73.9934,
        IReadOnlyList<string>? lineup = null,
        string? ticketUrl = null,
        decimal? priceMin = null,
        decimal? priceMax = null,
        string? currency = null,
        DateTimeOffset? onSaleAt = null,
        DateTimeOffset? fetchedAt = null,
        DateTimeOffset? lastSeenAt = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new ConcertRecord(
            Id:            id ?? Guid.NewGuid().ToString(),
            Source:        source,
            SourceEventId: sourceEventId,
            SourceUrl:     sourceUrl,
            ArtistMbid:    artistMbid,
            ArtistName:    artistName,
            EventDateTime: eventDateTime ?? BaseTime,
            VenueName:     venueName,
            VenueAddress:  null,
            City:          city,
            Region:        region,
            Country:       country,
            Lat:           lat,
            Lon:           lon,
            Lineup:        lineup ?? new List<string> { artistName },
            TicketUrl:     ticketUrl ?? sourceUrl,
            PriceMin:      priceMin,
            PriceMax:      priceMax,
            Currency:      currency,
            OnSaleAt:      onSaleAt,
            FetchedAt:     fetchedAt ?? now,
            LastSeenAt:    lastSeenAt ?? now);
    }
}
