using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.ConcertRadar.Configuration;
using Jellyfin.Plugin.ConcertRadar.Model;

namespace Jellyfin.Plugin.ConcertRadar.Normalization;

/// <summary>
/// Converts a <see cref="RawEvent"/> into a <see cref="ConcertRecord"/>, validating mandatory
/// fields and applying post-fetch filters.  All methods are pure — no IO occurs here.
/// </summary>
public static class Normalizer
{
    /// <summary>
    /// Normalizes <paramref name="rawEvent"/> into a <see cref="ConcertRecord"/>.
    /// </summary>
    /// <param name="rawEvent">The raw event from the source adapter.</param>
    /// <param name="source">Source identifier (e.g. "ticketmaster").</param>
    /// <param name="artist">Resolved artist reference.</param>
    /// <param name="filter">Post-fetch filter criteria.</param>
    /// <param name="now">Current UTC time, used as <c>FetchedAt</c> and <c>LastSeenAt</c>.</param>
    /// <returns>
    /// A <see cref="ConcertRecord"/>, or <c>null</c> when the event is filtered out by
    /// <paramref name="filter"/> rules.
    /// </returns>
    /// <exception cref="ValidationException">
    /// When any mandatory field (SourceUrl, EventDateTime, ArtistName) is missing.
    /// </exception>
    public static ConcertRecord? Normalize(
        RawEvent rawEvent,
        string source,
        ArtistRef artist,
        SourceFilter filter,
        DateTimeOffset now)
    {
        // ── Mandatory-field validation ─────────────────────────────────────────
        if (string.IsNullOrWhiteSpace(rawEvent.SourceUrl))
            throw new ValidationException("RawEvent.SourceUrl is required.");

        if (rawEvent.EventDateTime == default)
            throw new ValidationException("RawEvent.EventDateTime is required.");

        if (string.IsNullOrWhiteSpace(rawEvent.ArtistName))
            throw new ValidationException("RawEvent.ArtistName is required.");

        // ── Post-fetch filters ────────────────────────────────────────────────
        if (filter.CountryAllowlist.Count > 0 &&
            !filter.CountryAllowlist.Contains(rawEvent.Country ?? string.Empty, StringComparer.OrdinalIgnoreCase))
        {
            return null;
        }

        if (rawEvent.EventDateTime < filter.MinDate || rawEvent.EventDateTime > filter.MaxDate)
            return null;

        if (filter.SkipFestivals && rawEvent.IsFestival)
            return null;

        if (filter.SkipSoldOut && rawEvent.IsSoldOut)
            return null;

        // Genre allowlist: only enforce when the event carries genre data.
        // If the RawEvent has no genre info, we cannot determine genre → do not drop.
        if (filter.GenreAllowlist.Count > 0 &&
            rawEvent.Genres is { Count: > 0 } eventGenres)
        {
            bool genreMatch = false;
            foreach (var g in eventGenres)
            {
                if (filter.GenreAllowlist.Contains(g))
                {
                    genreMatch = true;
                    break;
                }
            }

            if (!genreMatch)
                return null;
        }

        // Location filter: when locations are specified, keep the event only if it falls
        // within radius of at least one location. Fall back to city/country string match when
        // lat/lon is absent. If both are missing, do not drop (cannot determine location).
        if (filter.Locations.Count > 0 &&
            !MatchesAnyLocation(rawEvent, filter.Locations))
        {
            return null;
        }

        // ── Stable ID derivation ──────────────────────────────────────────────
        string id = DeriveStableId(source, rawEvent.SourceEventId);

        // ── Build record ──────────────────────────────────────────────────────
        return new ConcertRecord(
            Id:            id,
            Source:        source,
            SourceEventId: rawEvent.SourceEventId,
            SourceUrl:     rawEvent.SourceUrl,
            ArtistMbid:    artist.Mbid,
            ArtistName:    rawEvent.ArtistName,
            EventDateTime: rawEvent.EventDateTime,
            VenueName:     rawEvent.VenueName,
            VenueAddress:  rawEvent.VenueAddress,
            City:          rawEvent.City,
            Region:        rawEvent.Region,
            Country:       rawEvent.Country,
            Lat:           rawEvent.Lat,
            Lon:           rawEvent.Lon,
            Lineup:        rawEvent.Lineup,
            TicketUrl:     rawEvent.TicketUrl,
            PriceMin:      rawEvent.PriceMin,
            PriceMax:      rawEvent.PriceMax,
            Currency:      rawEvent.Currency,
            OnSaleAt:      rawEvent.OnSaleAt,
            FetchedAt:     now,
            LastSeenAt:    now);
    }

    /// <summary>
    /// Returns <c>true</c> if <paramref name="rawEvent"/> falls within the radius of at least
    /// one entry in <paramref name="locations"/>. When lat/lon is absent, falls back to
    /// case-insensitive city and country string matching. Returns <c>true</c> (do not drop)
    /// when neither lat/lon nor city/country is available.
    /// </summary>
    private static bool MatchesAnyLocation(RawEvent rawEvent, IReadOnlyList<LocationFilter> locations)
    {
        foreach (var loc in locations)
        {
            // Prefer haversine when both event and filter carry coordinates.
            if (rawEvent.Lat.HasValue && rawEvent.Lon.HasValue &&
                loc.Lat.HasValue && loc.Lon.HasValue)
            {
                double distKm = HaversineKm(rawEvent.Lat.Value, rawEvent.Lon.Value,
                    loc.Lat.Value, loc.Lon.Value);
                if (distKm <= loc.RadiusKm)
                    return true;

                continue; // coord match failed; try next location
            }

            // Fallback: city and/or country string equality.
            bool cityMatch = string.IsNullOrWhiteSpace(loc.City) ||
                string.Equals(rawEvent.City, loc.City, StringComparison.OrdinalIgnoreCase);
            bool countryMatch = string.IsNullOrWhiteSpace(loc.Country) ||
                string.Equals(rawEvent.Country, loc.Country, StringComparison.OrdinalIgnoreCase);

            if (string.IsNullOrWhiteSpace(loc.City) && string.IsNullOrWhiteSpace(loc.Country))
            {
                // Location filter specifies only coordinates but the event has none — cannot filter.
                return true;
            }

            if (cityMatch && countryMatch)
                return true;
        }

        // No location matched; if the event has no location data at all, do not drop.
        if (string.IsNullOrWhiteSpace(rawEvent.City) &&
            string.IsNullOrWhiteSpace(rawEvent.Country) &&
            !rawEvent.Lat.HasValue && !rawEvent.Lon.HasValue)
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Haversine great-circle distance in kilometres between two WGS-84 coordinates.
    /// </summary>
    private static double HaversineKm(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6371.0; // Earth radius km
        double dLat = ToRad(lat2 - lat1);
        double dLon = ToRad(lon2 - lon1);
        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                   Math.Cos(ToRad(lat1)) * Math.Cos(ToRad(lat2)) *
                   Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    private static double ToRad(double deg) => deg * Math.PI / 180.0;

    /// <summary>
    /// Derives a stable deterministic <see cref="Guid"/> from the source and source event id
    /// so that repeated fetches of the same event always produce the same primary key, enabling
    /// idempotent upserts in <c>ConcertRepository</c>.
    /// </summary>
    /// <remarks>
    /// Algorithm: SHA-256 of UTF-8(<paramref name="source"/> + "|" + <paramref name="sourceEventId"/>),
    /// first 16 bytes cast to a <see cref="Guid"/>.
    /// </remarks>
    public static string DeriveStableId(string source, string sourceEventId)
    {
        var input = Encoding.UTF8.GetBytes($"{source}|{sourceEventId}");
        var hash  = SHA256.HashData(input);
        // Take the first 16 bytes of the SHA-256 hash as a Guid.
        return new Guid(hash.AsSpan(0, 16)).ToString();
    }
}
