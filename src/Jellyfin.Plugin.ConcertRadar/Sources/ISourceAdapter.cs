using System.Collections.Generic;
using System.Threading;
using Jellyfin.Plugin.ConcertRadar.Configuration;
using Jellyfin.Plugin.ConcertRadar.Model;

namespace Jellyfin.Plugin.ConcertRadar.Sources;

/// <summary>
/// Contract that every concert-data source adapter must implement.
/// Adapters are registered in DI as <see cref="ISourceAdapter"/> and resolved as
/// <see cref="IEnumerable{T}"/> of <see cref="ISourceAdapter"/> by the scheduler.
/// </summary>
public interface ISourceAdapter
{
    /// <summary>Gets the stable lower-case identifier for this source (e.g. "ticketmaster").</summary>
    string Id { get; }

    /// <summary>Gets the human-readable display name shown in the admin UI.</summary>
    string DisplayName { get; }

    /// <summary>Gets whether this source uses an official API or HTML/JSON scraping.</summary>
    SourceKind Kind { get; }

    /// <summary>
    /// Gets a value indicating whether this adapter requires credentials
    /// (API key, app_id, etc.) to operate.
    /// </summary>
    bool RequiresCredentials { get; }

    /// <summary>
    /// Gets a value indicating whether the operator must explicitly opt in to this adapter's
    /// terms-of-service before it will fetch data.
    /// </summary>
    bool RequiresTosOptIn { get; }

    /// <summary>
    /// Returns <c>true</c> when the adapter has enough configuration (API keys, etc.)
    /// to make requests.
    /// </summary>
    /// <param name="cfg">Current plugin configuration.</param>
    bool IsConfigured(PluginConfiguration cfg);

    /// <summary>
    /// Fetches upcoming events for <paramref name="artist"/> from this source.
    /// </summary>
    /// <param name="artist">Artist to query.</param>
    /// <param name="filter">Active source filter (date range, country allowlist, etc.).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An async stream of raw events as returned by the source.</returns>
    IAsyncEnumerable<RawEvent> FetchAsync(
        ArtistRef artist,
        SourceFilter filter,
        CancellationToken ct);
}
