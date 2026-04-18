using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
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
