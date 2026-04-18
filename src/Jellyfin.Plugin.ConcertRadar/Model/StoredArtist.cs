using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.ConcertRadar.Model;

/// <summary>
/// Maps a row in the <c>artists</c> table.
/// </summary>
/// <param name="Id">Primary key: MBID when present, otherwise SHA-1 of the lower-cased name.</param>
/// <param name="Mbid">MusicBrainz artist ID (nullable).</param>
/// <param name="Name">Artist display name.</param>
/// <param name="JellyfinItemId">Jellyfin MusicArtist item GUID (nullable).</param>
/// <param name="ExternalIds">Map of source identifier to external artist ID (e.g. Songkick numeric ID).</param>
/// <param name="LastCheckedAt">When this artist was last processed by the scheduler (null = never).</param>
/// <param name="LastError">Last error message from a refresh attempt (null = none).</param>
/// <param name="ConsecutiveErrors">Number of consecutive refresh failures.</param>
public sealed record StoredArtist(
    string Id,
    string? Mbid,
    string Name,
    Guid? JellyfinItemId,
    IReadOnlyDictionary<string, string> ExternalIds,
    DateTimeOffset? LastCheckedAt,
    string? LastError,
    int ConsecutiveErrors);
