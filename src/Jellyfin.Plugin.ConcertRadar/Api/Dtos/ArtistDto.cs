using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.ConcertRadar.Model;

namespace Jellyfin.Plugin.ConcertRadar.Api.Dtos;

/// <summary>Artist summary returned by the API.</summary>
public sealed record ArtistDto
{
    /// <summary>Gets the primary key (MBID or SHA-1 of name).</summary>
    public required string Id { get; init; }

    /// <summary>Gets the MusicBrainz artist ID (nullable).</summary>
    public string? Mbid { get; init; }

    /// <summary>Gets the artist display name.</summary>
    public required string Name { get; init; }

    /// <summary>Gets when this artist was last processed (null = never).</summary>
    public DateTimeOffset? LastCheckedAt { get; init; }

    /// <summary>Gets the list of source IDs for which external IDs have been resolved.</summary>
    public required IReadOnlyList<string> ResolvedSources { get; init; }

    /// <summary>Maps a <see cref="StoredArtist"/> to <see cref="ArtistDto"/>.</summary>
    public static ArtistDto FromStored(StoredArtist a) => new()
    {
        Id              = a.Id,
        Mbid            = a.Mbid,
        Name            = a.Name,
        LastCheckedAt   = a.LastCheckedAt,
        ResolvedSources = a.ExternalIds.Keys.ToList(),
    };
}
