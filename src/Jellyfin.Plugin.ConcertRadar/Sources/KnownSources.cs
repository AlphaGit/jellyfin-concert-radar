using System.Collections.Immutable;

namespace Jellyfin.Plugin.ConcertRadar.Sources;

/// <summary>
/// Single source of truth for the known source identifiers used by this plugin.
/// Used to validate path parameters in the admin API and to bound dynamic dictionary growth
/// in <see cref="RateLimiting.HostRateLimiter"/>.
/// </summary>
internal static class KnownSources
{
    /// <summary>
    /// The six user-facing source IDs plus the internal <c>musicbrainz</c> resolver ID.
    /// </summary>
    public static readonly ImmutableHashSet<string> Ids =
        ImmutableHashSet.Create(
            StringComparer.OrdinalIgnoreCase,
            "ticketmaster",
            "bandsintown",
            "edmtrain",
            "songkick",
            "dice",
            "ra");
}
