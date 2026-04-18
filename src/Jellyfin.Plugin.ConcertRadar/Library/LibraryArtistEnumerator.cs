using System;
using System.Collections.Generic;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.ConcertRadar.Model;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ConcertRadar.Library;

/// <summary>
/// Enumerates all MusicArtist items in the Jellyfin library and yields them as
/// <see cref="LibraryArtist"/> records suitable for artist-resolution and concert lookup.
/// </summary>
public sealed class LibraryArtistEnumerator
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<LibraryArtistEnumerator> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryArtistEnumerator"/> class.
    /// </summary>
    /// <param name="libraryManager">Jellyfin library manager.</param>
    /// <param name="logger">Logger.</param>
    public LibraryArtistEnumerator(
        ILibraryManager libraryManager,
        ILogger<LibraryArtistEnumerator> logger)
    {
        _libraryManager = libraryManager;
        _logger         = logger;
    }

    /// <summary>
    /// Returns all music artists found in the Jellyfin library.
    /// If a MusicBrainz provider ID is present, its first value (comma-split, trimmed) is used as
    /// the MBID.  Artists without a provider ID have a null MBID.
    /// </summary>
    public IEnumerable<LibraryArtist> Enumerate()
    {
        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.MusicArtist },
            Recursive = true,
        });

        int total = 0;
        int withMbid = 0;

        foreach (var item in items)
        {
            total++;
            string? mbid = null;

            if (item.ProviderIds.TryGetValue("MusicBrainzArtist", out var raw) &&
                !string.IsNullOrWhiteSpace(raw))
            {
                // Provider IDs may store multiple values comma-separated; take the first.
                int comma = raw.IndexOf(',', StringComparison.Ordinal);
                string candidate = comma >= 0
                    ? raw[..comma].Trim()
                    : raw.Trim();

                if (!string.IsNullOrEmpty(candidate))
                {
                    mbid = candidate;
                    withMbid++;
                }
            }

            yield return new LibraryArtist(item.Id, item.Name, mbid);
        }

        _logger.LogInformation(
            "LibraryArtistEnumerator: yielded {Total} artists, {WithMbid} with MBID.",
            total, withMbid);
    }
}
