using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AngleSharp;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Jellyfin.Plugin.ConcertRadar.Configuration;
using Jellyfin.Plugin.ConcertRadar.Model;
using Jellyfin.Plugin.ConcertRadar.RateLimiting;
using Jellyfin.Plugin.ConcertRadar.Storage;
using MediaBrowser.Common.Net;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ConcertRadar.Sources;

/// <summary>
/// Source adapter that scrapes upcoming concert events from Songkick.
/// Artist resolution prefers the <c>ext_ids["songkick"]</c> cached ID populated by
/// <see cref="Resolution.MusicBrainzResolver"/>; falls back to a search-page scrape.
/// </summary>
public sealed partial class SongkickScrapeAdapter : ISourceAdapter
{
    private const string SourceId = "songkick";
    private const string BaseUrl = "https://www.songkick.com";

    // Selector health-check: a page with events is expected to contain .event-listing elements.
    // If fewer than this fraction of expected selectors match, we flag Degraded.
    private const double DegradedThreshold = 0.20;

    private static readonly TimeSpan[] RetryDelays = AdapterDefaults.RetryBackoffs;

    private static readonly string UserAgent =
        "Mozilla/5.0 (compatible; JellyfinConcertRadar/0.1.0; +https://github.com/alphagma/jellyfin-concert-radar)";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly HostRateLimiter _rateLimiter;
    private readonly ArtistRepository _artistRepository;
    private readonly SourceStateRepository _sourceStateRepository;
    private readonly IPluginConfigurationProvider _configProvider;
    private readonly TimeProvider _clock;
    private readonly ILogger<SongkickScrapeAdapter> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SongkickScrapeAdapter"/> class.
    /// </summary>
    /// <param name="httpClientFactory">HTTP client factory.</param>
    /// <param name="rateLimiter">Shared rate limiter.</param>
    /// <param name="artistRepository">Artist repository for caching external IDs.</param>
    /// <param name="sourceStateRepository">Source state repository for circuit breaker and status.</param>
    /// <param name="configProvider">Plugin configuration provider.</param>
    /// <param name="clock">Time provider.</param>
    /// <param name="logger">Logger.</param>
    public SongkickScrapeAdapter(
        IHttpClientFactory httpClientFactory,
        HostRateLimiter rateLimiter,
        ArtistRepository artistRepository,
        SourceStateRepository sourceStateRepository,
        IPluginConfigurationProvider configProvider,
        TimeProvider clock,
        ILogger<SongkickScrapeAdapter> logger)
    {
        _httpClientFactory     = httpClientFactory;
        _rateLimiter           = rateLimiter;
        _artistRepository      = artistRepository;
        _sourceStateRepository = sourceStateRepository;
        _configProvider        = configProvider;
        _clock                 = clock;
        _logger                = logger;
    }

    /// <inheritdoc />
    public string Id => SourceId;

    /// <inheritdoc />
    public string DisplayName => "Songkick";

    /// <inheritdoc />
    public SourceKind Kind => SourceKind.Scrape;

    /// <inheritdoc />
    public bool RequiresCredentials => false;

    /// <inheritdoc />
    public bool RequiresTosOptIn => true;

    /// <inheritdoc />
    public bool IsConfigured(PluginConfiguration cfg) => cfg.AcceptSongkickScrapeTos;

    /// <inheritdoc />
    public async IAsyncEnumerable<RawEvent> FetchAsync(
        ArtistRef artist,
        SourceFilter filter,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // Step 1: resolve the Songkick artist page URL.
        string? artistPageUrl = await ResolveArtistUrlAsync(artist, ct).ConfigureAwait(false);
        if (artistPageUrl is null)
        {
            _logger.LogInformation(
                "SongkickScrapeAdapter: could not resolve Songkick URL for '{Name}'. Skipping.",
                artist.Name);
            yield break;
        }

        // Step 2: fetch the artist page and parse upcoming events.
        string? html;
        try
        {
            html = await GetWithRetryAsync(artistPageUrl, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SongkickScrapeAdapter: error fetching artist page '{Url}'.", artistPageUrl);
            throw;
        }

        if (html is null)
            yield break;

        // Step 3: parse HTML and yield RawEvent records.
        await foreach (var rawEvent in ParseArtistPageAsync(html, artist.Name, ct).ConfigureAwait(false))
        {
            yield return rawEvent;
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task<string?> ResolveArtistUrlAsync(ArtistRef artist, CancellationToken ct)
    {
        // Cache-first: use the ext_ids entry if available.
        if (artist.ExternalIds.TryGetValue(SourceId, out var cachedId) &&
            !string.IsNullOrWhiteSpace(cachedId))
        {
            return $"{BaseUrl}/artists/{cachedId}";
        }

        // Fallback: search Songkick by artist name.
        string encoded = Uri.EscapeDataString(artist.Name);
        string searchUrl = $"{BaseUrl}/search?query={encoded}&type=artists";

        string? searchHtml;
        try
        {
            searchHtml = await GetWithRetryAsync(searchUrl, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "SongkickScrapeAdapter: search failed for '{Name}'.", artist.Name);
            return null;
        }

        if (searchHtml is null)
            return null;

        var (artistSlug, artistUrl) = await ParseSearchResultAsync(searchHtml).ConfigureAwait(false);
        if (artistSlug is null || artistUrl is null)
        {
            _logger.LogInformation(
                "SongkickScrapeAdapter: no Songkick search result for '{Name}'.", artist.Name);
            return null;
        }

        // Persist the resolved ID for future runs.
        string dbId = ArtistRepository.ComputeId(artist.Mbid, artist.Name);
        await _artistRepository
            .SetExternalIdsAsync(dbId, new Dictionary<string, string> { [SourceId] = artistSlug }, ct)
            .ConfigureAwait(false);

        return artistUrl;
    }

    private static async Task<(string? slug, string? absoluteUrl)> ParseSearchResultAsync(string html)
    {
        var parser = new HtmlParser();
        using var doc = await parser.ParseDocumentAsync(html).ConfigureAwait(false);

        // Songkick search returns artist results under <a class="subject"> links.
        // The href looks like /artists/253846-radiohead
        var anchor = doc.QuerySelector("a.subject[href*='/artists/']")
                     ?? doc.QuerySelector(".artists-results a[href*='/artists/']");

        if (anchor is null)
            return (null, null);

        string href = anchor.GetAttribute("href") ?? string.Empty;

        // Extract the numeric id that is the stable cache key (e.g. "253846" from "/artists/253846-radiohead").
        var match = ArtistIdFromPath().Match(href);
        if (!match.Success)
            return (null, null);

        string slug = match.Value.TrimStart('/').Split('/')[1]; // "253846-radiohead"
        string absoluteUrl = new Uri(new Uri(BaseUrl), href).ToString();
        return (slug, absoluteUrl);
    }

    private async IAsyncEnumerable<RawEvent> ParseArtistPageAsync(
        string html,
        string artistName,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var parser = new HtmlParser();
        using var doc = await parser.ParseDocumentAsync(html).ConfigureAwait(false);

        // Detect "legitimately empty" pages.
        string bodyText = doc.Body?.TextContent ?? string.Empty;
        bool noUpcomingPhrase =
            bodyText.Contains("no upcoming events", StringComparison.OrdinalIgnoreCase) ||
            bodyText.Contains("no upcoming concerts", StringComparison.OrdinalIgnoreCase);

        // Gather all event-listing items.
        var listingItems = doc.QuerySelectorAll("li.event-listing");

        if (listingItems.Length == 0)
        {
            if (!noUpcomingPhrase)
            {
                // Non-empty page with no parseable events — could be a DOM drift.
                _logger.LogWarning(
                    "SongkickScrapeAdapter: no event-listing elements found on artist page. " +
                    "Possible DOM schema change. Reporting Degraded status.");
                await _sourceStateRepository
                    .UpsertStatusAsync(SourceId, SourceStatus.Degraded, ct)
                    .ConfigureAwait(false);
            }

            yield break;
        }

        int parsed = 0;
        int skipped = 0;

        foreach (var item in listingItems)
        {
            ct.ThrowIfCancellationRequested();
            var rawEvent = ParseEventListing(item, artistName);
            if (rawEvent is not null)
            {
                parsed++;
                yield return rawEvent;
            }
            else
            {
                skipped++;
            }
        }

        // Health heuristic: if more than 80% of listings failed to parse, report Degraded.
        int total = parsed + skipped;
        if (total > 0 && (double)skipped / total > (1.0 - DegradedThreshold))
        {
            _logger.LogWarning(
                "SongkickScrapeAdapter: {Skipped}/{Total} event listings failed to parse. " +
                "Reporting Degraded status.",
                skipped, total);
            await _sourceStateRepository
                .UpsertStatusAsync(SourceId, SourceStatus.Degraded, ct)
                .ConfigureAwait(false);
        }
    }

    private RawEvent? ParseEventListing(IElement item, string fallbackArtistName)
    {
        // Event URL from the primary link.
        var eventAnchor = item.QuerySelector("a.event-link")
                         ?? item.QuerySelector("a[href*='/concerts/']");
        string? relativeHref = eventAnchor?.GetAttribute("href");

        if (string.IsNullOrWhiteSpace(relativeHref))
        {
            _logger.LogDebug("SongkickScrapeAdapter: listing has no event link; skipping.");
            return null;
        }

        string sourceUrl = new Uri(new Uri(BaseUrl), relativeHref).ToString();

        // Extract SourceEventId from the URL path: /concerts/(\d+)
        var concertIdMatch = ConcertIdFromPath().Match(relativeHref);
        string sourceEventId = concertIdMatch.Success
            ? concertIdMatch.Groups[1].Value
            : relativeHref;

        // Event date/time from <time datetime="...">
        var timeEl = item.QuerySelector("time[datetime]");
        string? datetimeAttr = timeEl?.GetAttribute("datetime");

        if (string.IsNullOrWhiteSpace(datetimeAttr) ||
            !DateTimeOffset.TryParse(datetimeAttr, out DateTimeOffset eventDt))
        {
            _logger.LogDebug(
                "SongkickScrapeAdapter: could not parse datetime for event {Id}; skipping.",
                sourceEventId);
            return null;
        }

        // Venue, city, country from microformat blocks.
        string? venueName  = item.QuerySelector(".venue-name")?.TextContent?.Trim()
                          ?? item.QuerySelector("[itemprop='name']")?.TextContent?.Trim();
        string? city       = item.QuerySelector(".city-name")?.TextContent?.Trim()
                          ?? item.QuerySelector("[itemprop='addressLocality']")?.TextContent?.Trim();
        string? country    = item.QuerySelector(".country-name")?.TextContent?.Trim()
                          ?? item.QuerySelector("[itemprop='addressCountry']")?.TextContent?.Trim();

        if (string.IsNullOrWhiteSpace(city))
            _logger.LogDebug(
                "SongkickScrapeAdapter: no city found for event {Id}.", sourceEventId);
        if (string.IsNullOrWhiteSpace(country))
            _logger.LogDebug(
                "SongkickScrapeAdapter: no country found for event {Id}.", sourceEventId);

        // Lineup from the artists span.
        var artistsEl = item.QuerySelector("span.artists")
                     ?? item.QuerySelector("[itemprop='performer']");
        var lineup = new List<string>();
        if (artistsEl is not null)
        {
            string artistsText = artistsEl.TextContent.Trim();
            foreach (var name in artistsText.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                string trimmed = name.Trim();
                if (!string.IsNullOrWhiteSpace(trimmed))
                    lineup.Add(trimmed);
            }
        }

        string artistName = lineup.Count > 0 ? lineup[0] : fallbackArtistName;

        return new RawEvent(
            SourceEventId: sourceEventId,
            SourceUrl: sourceUrl,
            ArtistName: artistName,
            EventDateTime: eventDt,
            VenueName: string.IsNullOrWhiteSpace(venueName) ? null : venueName,
            VenueAddress: null,
            City: string.IsNullOrWhiteSpace(city) ? null : city,
            Region: null,
            Country: string.IsNullOrWhiteSpace(country) ? null : country,
            Lat: null,
            Lon: null,
            Lineup: lineup,
            TicketUrl: null,
            PriceMin: null,
            PriceMax: null,
            Currency: null,
            OnSaleAt: null,
            IsFestival: false,
            IsSoldOut: false);
    }

    private async Task<string?> GetWithRetryAsync(string url, CancellationToken ct)
    {
        Exception? lastEx = null;

        for (int attempt = 0; attempt <= RetryDelays.Length; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            if (attempt > 0)
                await Task.Delay(RetryDelays[attempt - 1], ct).ConfigureAwait(false);

            await _rateLimiter.AcquireAsync(SourceId, ct).ConfigureAwait(false);

            using var client = _httpClientFactory.CreateClient(NamedClient.Default);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");

            HttpResponseMessage response;
            try
            {
                response = await client.GetAsync(url, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex,
                    "SongkickScrapeAdapter: network error on attempt {Attempt}.", attempt + 1);
                lastEx = ex;
                continue;
            }

            await _rateLimiter.RecordCallAsync(SourceId, ct).ConfigureAwait(false);

            // Cloudflare-style hard 403 — do not retry, yield nothing.
            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                _logger.LogWarning(
                    "SongkickScrapeAdapter: received 403 Forbidden for '{Url}'. " +
                    "Possible Cloudflare block. Aborting without retry.", url);
                return null;
            }

            if (response.IsSuccessStatusCode)
            {
                string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                return body;
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests ||
                (int)response.StatusCode >= 500)
            {
                if (response.Headers.RetryAfter is { } retryAfter)
                {
                    DateTimeOffset until = retryAfter.Date
                        ?? _clock.GetUtcNow().Add(retryAfter.Delta ?? AdapterDefaults.DefaultRetryAfterFallback);
                    await _rateLimiter.SetBackoffAsync(SourceId, until, ct).ConfigureAwait(false);
                }

                lastEx = new HttpRequestException(
                    $"Songkick returned {(int)response.StatusCode}.",
                    null,
                    response.StatusCode);
                _logger.LogWarning(
                    "SongkickScrapeAdapter: transient error {Status} on attempt {Attempt}.",
                    response.StatusCode, attempt + 1);
                continue;
            }

            // Non-transient, non-403 error — fail immediately.
            response.EnsureSuccessStatusCode();
        }

        throw new HttpRequestException(
            $"Songkick request failed after {RetryDelays.Length + 1} attempts: {UrlRedactor.Redact(url)}",
            lastEx);
    }

    // ── Compiled regexes ──────────────────────────────────────────────────────

    [GeneratedRegex(@"/artists/[\w\-]+")]
    private static partial Regex ArtistIdFromPath();

    [GeneratedRegex(@"/concerts/(\d+)")]
    private static partial Regex ConcertIdFromPath();
}
