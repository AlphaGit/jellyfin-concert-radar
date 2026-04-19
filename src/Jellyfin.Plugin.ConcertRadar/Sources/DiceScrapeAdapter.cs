using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AngleSharp.Html.Parser;
using Jellyfin.Plugin.ConcertRadar.Configuration;
using Jellyfin.Plugin.ConcertRadar.Model;
using Jellyfin.Plugin.ConcertRadar.RateLimiting;
using Jellyfin.Plugin.ConcertRadar.Storage;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ConcertRadar.Sources;

/// <summary>
/// Source adapter that scrapes upcoming concert events from Dice.fm by extracting the
/// embedded <c>__NEXT_DATA__</c> JSON blob from the server-rendered artist page.
/// This adapter must be explicitly enabled by the operator via <see cref="PluginConfiguration.AcceptDiceScrapeTos"/>.
/// </summary>
public sealed class DiceScrapeAdapter : ISourceAdapter
{
    private const string SourceId = "dice";
    private const string BaseUrl = "https://dice.fm";

    private static readonly TimeSpan[] RetryDelays = AdapterDefaults.RetryBackoffs;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly string UserAgent =
        "Mozilla/5.0 (compatible; JellyfinConcertRadar/0.1.0; +https://github.com/alphagma/jellyfin-concert-radar)";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly HostRateLimiter _rateLimiter;
    private readonly ArtistRepository _artistRepository;
    private readonly SourceStateRepository _sourceStateRepository;
    private readonly IPluginConfigurationProvider _configProvider;
    private readonly TimeProvider _clock;
    private readonly ILogger<DiceScrapeAdapter> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DiceScrapeAdapter"/> class.
    /// </summary>
    /// <param name="httpClientFactory">HTTP client factory.</param>
    /// <param name="rateLimiter">Shared rate limiter.</param>
    /// <param name="artistRepository">Artist repository for caching external IDs.</param>
    /// <param name="sourceStateRepository">Source state repository for circuit breaker.</param>
    /// <param name="configProvider">Plugin configuration provider.</param>
    /// <param name="clock">Time provider.</param>
    /// <param name="logger">Logger.</param>
    public DiceScrapeAdapter(
        IHttpClientFactory httpClientFactory,
        HostRateLimiter rateLimiter,
        ArtistRepository artistRepository,
        SourceStateRepository sourceStateRepository,
        IPluginConfigurationProvider configProvider,
        TimeProvider clock,
        ILogger<DiceScrapeAdapter> logger)
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
    public string DisplayName => "Dice.fm";

    /// <inheritdoc />
    public SourceKind Kind => SourceKind.Scrape;

    /// <inheritdoc />
    public bool RequiresCredentials => false;

    /// <inheritdoc />
    public bool RequiresTosOptIn => true;

    /// <inheritdoc />
    public bool IsConfigured(PluginConfiguration cfg) => cfg.AcceptDiceScrapeTos;

    /// <inheritdoc />
    public async IAsyncEnumerable<RawEvent> FetchAsync(
        ArtistRef artist,
        SourceFilter filter,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var cfg = _configProvider.GetConfiguration();
        if (cfg is null || !IsConfigured(cfg))
            yield break;

        // Step 1: resolve the Dice.fm artist slug.
        string? slug = await ResolveSlugAsync(artist, ct).ConfigureAwait(false);
        if (slug is null)
        {
            _logger.LogInformation(
                "DiceScrapeAdapter: could not resolve Dice.fm slug for '{Name}'. Skipping.",
                artist.Name);
            yield break;
        }

        // Step 2: fetch the artist page.
        string artistUrl = $"{BaseUrl}/artist/{slug}";
        (string? html, bool cloudflareBlocked) = await GetHtmlAsync(artistUrl, ct).ConfigureAwait(false);

        if (cloudflareBlocked)
        {
            _logger.LogWarning(
                "DiceScrapeAdapter: Cloudflare challenge detected for '{Url}'. Aborting.", artistUrl);
            yield break;
        }

        if (html is null)
            yield break;

        // Step 3: extract __NEXT_DATA__ and walk for events.
        JsonNode? nextData = ExtractNextData(html);
        if (nextData is null)
        {
            _logger.LogWarning(
                "DiceScrapeAdapter: could not find __NEXT_DATA__ on artist page '{Url}'.", artistUrl);
            yield break;
        }

        JsonArray? events = FindEventsArray(nextData);
        if (events is null || events.Count == 0)
        {
            _logger.LogInformation(
                "DiceScrapeAdapter: no events found in __NEXT_DATA__ for '{Name}'.", artist.Name);
            yield break;
        }

        foreach (var eventNode in events)
        {
            ct.ThrowIfCancellationRequested();
            if (eventNode is null) continue;

            var rawEvent = MapToRawEvent(eventNode, artist.Name);
            if (rawEvent is not null)
                yield return rawEvent;
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task<string?> ResolveSlugAsync(ArtistRef artist, CancellationToken ct)
    {
        // Cache-first.
        if (artist.ExternalIds.TryGetValue(SourceId, out var cached) &&
            !string.IsNullOrWhiteSpace(cached))
        {
            return cached;
        }

        // Search Dice.fm for the artist.
        string encoded = Uri.EscapeDataString(artist.Name);
        string searchUrl = $"{BaseUrl}/search?q={encoded}";

        (string? html, bool blocked) = await GetHtmlAsync(searchUrl, ct).ConfigureAwait(false);
        if (blocked || html is null)
            return null;

        JsonNode? nextData = ExtractNextData(html);
        if (nextData is null)
            return null;

        string? slug = FindArtistSlugInSearch(nextData);
        if (slug is null)
        {
            _logger.LogInformation(
                "DiceScrapeAdapter: no artist slug found in search results for '{Name}'.", artist.Name);
            return null;
        }

        // Persist for future runs.
        string dbId = ArtistRepository.ComputeId(artist.Mbid, artist.Name);
        await _artistRepository
            .SetExternalIdsAsync(dbId, new Dictionary<string, string> { [SourceId] = slug }, ct)
            .ConfigureAwait(false);

        return slug;
    }

    private static string? FindArtistSlugInSearch(JsonNode root)
    {
        // Try several known candidate paths for the artists collection in the search results.
        string[][] candidatePaths =
        [
            ["props", "pageProps", "searchResults", "artists"],
            ["props", "pageProps", "initialData", "artists"],
            ["props", "pageProps", "artists"],
        ];

        foreach (var path in candidatePaths)
        {
            JsonNode? node = root;
            foreach (var segment in path)
            {
                node = node?[segment];
                if (node is null) break;
            }

            if (node is JsonArray arr && arr.Count > 0)
            {
                // First result: try slug or url field.
                var first = arr[0];
                string? slug = first?["slug"]?.GetValue<string>()
                             ?? first?["url"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(slug))
                    return slug;
            }
        }

        return null;
    }

    private static JsonArray? FindEventsArray(JsonNode root)
    {
        // Try candidate paths for the events collection on the artist page.
        string[][] candidatePaths =
        [
            ["props", "pageProps", "artist", "events"],
            ["props", "pageProps", "initialState", "events"],
            ["props", "pageProps", "events"],
        ];

        foreach (var path in candidatePaths)
        {
            JsonNode? node = root;
            foreach (var segment in path)
            {
                node = node?[segment];
                if (node is null) break;
            }

            if (node is JsonArray arr && arr.Count > 0)
                return arr;
        }

        return null;
    }

    private RawEvent? MapToRawEvent(JsonNode ev, string fallbackArtistName)
    {
        string? idRaw = ev["id"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(idRaw))
        {
            _logger.LogDebug("DiceScrapeAdapter: event has no id; skipping.");
            return null;
        }

        // Build the canonical event URL.
        string? permName = ev["perm_name"]?.GetValue<string>()
                        ?? ev["url"]?.GetValue<string>();
        string sourceUrl = !string.IsNullOrWhiteSpace(permName)
            ? (permName.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? permName
                : $"{BaseUrl}/event/{permName}")
            : $"{BaseUrl}/event/{idRaw}";

        // Validate that the final URL host is dice.fm (or a subdomain). Drop events with
        // unexpected hosts to prevent open-redirect via a compromised upstream.
        if (!UrlGuard.IsHostAllowed(sourceUrl, "dice.fm"))
        {
            _logger.LogWarning(
                "DiceScrapeAdapter: event {Id} has unexpected host in sourceUrl '{Url}'; dropping.",
                idRaw, sourceUrl);
            return null;
        }

        // Parse event date/time.
        string? dateStr = ev["date"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(dateStr) ||
            !DateTimeOffset.TryParse(dateStr, out DateTimeOffset eventDt))
        {
            _logger.LogDebug(
                "DiceScrapeAdapter: could not parse date for event {Id}; skipping.", idRaw);
            return null;
        }

        // Venue fields.
        var venue = ev["venue"];
        string? venueName    = venue?["name"]?.GetValue<string>();
        string? venueAddress = venue?["address_line_1"]?.GetValue<string>();
        var venueCity        = venue?["city"];
        string? city         = venueCity?["name"]?.GetValue<string>();
        string? country      = venueCity?["country_name"]?.GetValue<string>();

        // Coordinates.
        double? lat = null;
        double? lon = null;
        var location = venue?["location"];
        if (location is not null)
        {
            lat = location["latitude"]?.GetValue<double?>();
            lon = location["longitude"]?.GetValue<double?>();
        }

        // Lineup from "lineup" or "artists" arrays.
        var lineup = new List<string>();
        JsonArray? lineupArr = ev["lineup"] as JsonArray ?? ev["artists"] as JsonArray;
        if (lineupArr is not null)
        {
            foreach (var entry in lineupArr)
            {
                string? name = entry?["name"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(name))
                    lineup.Add(name);
            }
        }

        string artistName = lineup.Count > 0 ? lineup[0] : fallbackArtistName;

        // Sold-out: inverted from tickets.available.
        bool isSoldOut = false;
        bool? ticketsAvailable = ev["tickets"]?["available"]?.GetValue<bool?>();
        if (ticketsAvailable.HasValue)
            isSoldOut = !ticketsAvailable.Value;

        // Ticket URL (may differ from source URL).
        string? ticketUrl = ev["ticket_url"]?.GetValue<string>();

        return new RawEvent(
            SourceEventId: idRaw,
            SourceUrl: sourceUrl,
            ArtistName: artistName,
            EventDateTime: eventDt,
            VenueName: string.IsNullOrWhiteSpace(venueName) ? null : venueName,
            VenueAddress: string.IsNullOrWhiteSpace(venueAddress) ? null : venueAddress,
            City: string.IsNullOrWhiteSpace(city) ? null : city,
            Region: null,
            Country: string.IsNullOrWhiteSpace(country) ? null : country,
            Lat: lat,
            Lon: lon,
            Lineup: lineup,
            TicketUrl: string.IsNullOrWhiteSpace(ticketUrl) ? null : ticketUrl,
            PriceMin: null,
            PriceMax: null,
            Currency: null,
            OnSaleAt: null,
            IsFestival: false,
            IsSoldOut: isSoldOut);
    }

    private static JsonNode? ExtractNextData(string html)
    {
        // Use AngleSharp to safely extract the __NEXT_DATA__ script content.
        var parser = new HtmlParser();
        using var doc = parser.ParseDocument(html);

        var scriptEl = doc.QuerySelector("script#__NEXT_DATA__[type='application/json']");
        string? jsonText = scriptEl?.TextContent;
        if (string.IsNullOrWhiteSpace(jsonText))
            return null;

        try
        {
            return JsonNode.Parse(jsonText);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsCloudflareChallenge(HttpResponseMessage response, string? body)
    {
        if (response.StatusCode == HttpStatusCode.Forbidden)
            return true;

        if (body is not null)
        {
            return body.Contains("<title>Just a moment...</title>", StringComparison.OrdinalIgnoreCase)
                || body.Contains("cf-ray", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    /// <summary>
    /// Returns the HTML body and a Cloudflare-blocked flag.
    /// On Cloudflare challenge the method returns <c>(null, true)</c>.
    /// On transient errors it retries; on persistent failure it throws.
    /// </summary>
    private async Task<(string? body, bool cloudflareBlocked)> GetHtmlAsync(string url, CancellationToken ct)
    {
        Exception? lastEx = null;

        for (int attempt = 0; attempt <= RetryDelays.Length; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            if (attempt > 0)
                await Task.Delay(RetryDelays[attempt - 1], ct).ConfigureAwait(false);

            await _rateLimiter.AcquireAsync(SourceId, ct).ConfigureAwait(false);

            using var client = _httpClientFactory.CreateClient(PluginHttpClient.ClientName);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
            client.DefaultRequestHeaders.TryAddWithoutValidation(
                "Accept",
                "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");

            HttpResponseMessage response;
            try
            {
                response = await client.GetAsync(url, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex,
                    "DiceScrapeAdapter: network error on attempt {Attempt}.", attempt + 1);
                lastEx = ex;
                continue;
            }

            // Read body regardless of status so we can inspect Cloudflare markers.
            string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            // Hard Cloudflare block: do NOT retry.
            if (IsCloudflareChallenge(response, body))
                return (null, true);

            await _rateLimiter.RecordCallAsync(SourceId, ct).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                return (body, false);
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
                    $"Dice.fm returned {(int)response.StatusCode}.",
                    null,
                    response.StatusCode);
                _logger.LogWarning(
                    "DiceScrapeAdapter: transient error {Status} on attempt {Attempt}.",
                    response.StatusCode, attempt + 1);
                continue;
            }

            // Non-transient, non-Cloudflare error.
            response.EnsureSuccessStatusCode();
        }

        throw new HttpRequestException(
            $"Dice.fm request failed after {RetryDelays.Length + 1} attempts: {UrlRedactor.Redact(url)}",
            lastEx);
    }
}
