using System.Collections.Generic;

namespace Jellyfin.Plugin.ConcertRadar.Model;

/// <summary>
/// Lightweight artist reference passed from the scheduler to source adapters and the normalizer.
/// </summary>
/// <param name="Name">Display name of the artist.</param>
/// <param name="Mbid">MusicBrainz artist ID, or <c>null</c> if unresolved.</param>
/// <param name="ExternalIds">
/// Source-specific external IDs keyed by source id (e.g. <c>"songkick" → "253846"</c>).
/// </param>
public sealed record ArtistRef(
    string Name,
    string? Mbid,
    IReadOnlyDictionary<string, string> ExternalIds);
