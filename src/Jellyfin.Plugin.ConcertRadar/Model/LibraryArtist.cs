using System;

namespace Jellyfin.Plugin.ConcertRadar.Model;

/// <summary>
/// A music artist record sourced directly from the Jellyfin library.
/// </summary>
/// <param name="JellyfinItemId">The Jellyfin MusicArtist item GUID.</param>
/// <param name="Name">The artist's display name.</param>
/// <param name="Mbid">The MusicBrainz artist ID, if available.</param>
public sealed record LibraryArtist(Guid JellyfinItemId, string Name, string? Mbid);
