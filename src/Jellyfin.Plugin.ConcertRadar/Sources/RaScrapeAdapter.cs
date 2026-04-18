using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ConcertRadar.Configuration;
using Jellyfin.Plugin.ConcertRadar.Model;
using Jellyfin.Plugin.ConcertRadar.RateLimiting;
using Jellyfin.Plugin.ConcertRadar.Storage;
using MediaBrowser.Common.Net;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ConcertRadar.Sources;

/// <summary>
/// Source adapter that queries Resident Advisor's reverse-engineered GraphQL endpoint
/// to fetch upcoming events for an artist.
/// This adapter must be explicitly enabled by the operator via <see cref="PluginConfiguration.AcceptRaScrapeTos"/>.
/// The RA ToS warning banner in admin.html is already present (added in T4.5).
/// </summary>
public sealed class RaScrapeAdapter : ISourceAdapter
{
    private const string SourceId = "ra";
    private const string GraphQlUrl = "https://ra.co/graphql";
    private const int PageSize = 100;
    private const int MaxPages = 5;

    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromMilliseconds(200),
        TimeSpan.FromMilliseconds(800),
        TimeSpan.FromMilliseconds(3200),
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly string UserAgent =
        "Mozilla/5.0 (compatible; JellyfinConcertRadar/0.1.0; +https://github.com/alphagma/jellyfin-concert-radar)";

    // The eventListings GraphQL query used to fetch events by artist slug.
    private const string EventListingsQuery = """
        query GET_EVENT_LISTINGS($filters: FilterInputDtoInput!, $pageSize: Int!, $page: Int!) {
          eventListings(filters: $filters, pageSize: $pageSize, page: $page) {
            data {
              event {
                id
                title
                contentUrl
                date
                startTime
                endTime
                venue {
                  name
                  address
                  area {
                    name
                    country {
                      name
                    }
                  }
                }
                artists {
                  name
                }
              }
            }
            totalResults
          }
        }
        """;

    // GraphQL query used to look up an artist by name when no slug is cached.
    private const string ArtistSearchQuery = """
        query SEARCH_ARTIST($name: String!) {
          artistSearch(query: $name) {
            data {
              id
              name
              urlSlug
            }
          }
        }
        """;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly HostRateLimiter _rateLimiter;
    private readonly ArtistRepository _artistRepository;
    private readonly SourceStateRepository _sourceStateRepository;
    private readonly ILogger<RaScrapeAdapter> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RaScrapeAdapter"/> class.
    /// </summary>
    /// <param name="httpClientFactory">HTTP client factory.</param>
    /// <param name="rateLimiter">Shared rate limiter.</param>
    /// <param name="artistRepository">Artist repository for caching external IDs.</param>
    /// <param name="sourceStateRepository">Source state repository for circuit breaker.</param>
    /// <param name="logger">Logger.</param>
    public RaScrapeAdapter(
        IHttpClientFactory httpClientFactory,
        HostRateLimiter rateLimiter,
        ArtistRepository artistRepository,
        SourceStateRepository sourceStateRepository,
        ILogger<RaScrapeAdapter> logger)
    {
        _httpClientFactory     = httpClientFactory;
        _rateLimiter           = rateLimiter;
        _artistRepository      = artistRepository;
        _sourceStateRepository = sourceStateRepository;
        _logger                = logger;
    }

    /// <inheritdoc />
    public string Id => SourceId;

    /// <inheritdoc />
    public string DisplayName => "Resident Advisor";

    /// <inheritdoc />
    public SourceKind Kind => SourceKind.Scrape;

    /// <inheritdoc />
    public bool RequiresCredentials => false;

    /// <inheritdoc />
    public bool RequiresTosOptIn => true;

    /// <inheritdoc />
    public bool IsConfigured(PluginConfiguration cfg) => cfg.AcceptRaScrapeTos;

    /// <inheritdoc />
    public async IAsyncEnumerable<RawEvent> FetchAsync(
        ArtistRef artist,
        SourceFilter filter,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null || !IsConfigured(cfg))
            yield break;

        // Step 1: resolve the RA artist slug.
        string? slug = await ResolveSlugAsync(artist, ct).ConfigureAwait(false);
        if (slug is null)
        {
            _logger.LogInformation(
                "RaScrapeAdapter: could not resolve RA slug for '{Name}'. Skipping.",
                artist.Name);
            yield break;
        }

        // Step 2: paginate event listings.
        int page = 1;
        int totalResults = int.MaxValue;

        while (page <= MaxPages)
        {
            ct.ThrowIfCancellationRequested();

            int fetched = 0 + (page - 1) * PageSize;
            if (fetched >= totalResults)
                break;

            var variables = new
            {
                filters = new
                {
                    listingDateGte = filter.MinDate.UtcDateTime.ToString("yyyy-MM-dd"),
                    listingDateLte = filter.MaxDate.UtcDateTime.ToString("yyyy-MM-dd"),
                    artist = slug,
                },
                pageSize = PageSize,
                page,
            };

            JsonNode? responseNode;
            try
            {
                responseNode = await PostGraphQlAsync(EventListingsQuery, variables, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "RaScrapeAdapter: error fetching events page {Page} for '{Artist}'.",
                    page, artist.Name);
                throw;
            }

            var listingsNode = responseNode?["data"]?["eventListings"];
            if (listingsNode is null)
                break;

            // Read totalResults from the first page response.
            var totalNode = listingsNode["totalResults"];
            if (totalNode is not null)
                totalResults = totalNode.GetValue<int>();

            var dataArr = listingsNode["data"] as JsonArray;
            if (dataArr is null || dataArr.Count == 0)
                break;

            foreach (var listing in dataArr)
            {
                ct.ThrowIfCancellationRequested();
                var eventNode = listing?["event"];
                if (eventNode is null) continue;

                var rawEvent = MapToRawEvent(eventNode, artist.Name);
                if (rawEvent is not null)
                    yield return rawEvent;
            }

            page++;
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task<string?> ResolveSlugAsync(ArtistRef artist, CancellationToken ct)
    {
        // Cache-first from ext_ids.
        if (artist.ExternalIds.TryGetValue(SourceId, out var cached) &&
            !string.IsNullOrWhiteSpace(cached))
        {
            return cached;
        }

        // Use RA's artist search query.
        var variables = new { name = artist.Name };
        JsonNode? responseNode;
        try
        {
            responseNode = await PostGraphQlAsync(ArtistSearchQuery, variables, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "RaScrapeAdapter: artist search failed for '{Name}'.", artist.Name);
            return null;
        }

        var results = responseNode?["data"]?["artistSearch"]?["data"] as JsonArray;
        if (results is null || results.Count == 0)
        {
            _logger.LogInformation(
                "RaScrapeAdapter: no RA artist search result for '{Name}'.", artist.Name);
            return null;
        }

        // Pick best match (first result for now; RA search is generally accurate by name).
        string? slug = results[0]?["urlSlug"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(slug))
        {
            _logger.LogInformation(
                "RaScrapeAdapter: first RA search result has no urlSlug for '{Name}'.", artist.Name);
            return null;
        }

        // Persist for future runs.
        string dbId = ArtistRepository.ComputeId(artist.Mbid, artist.Name);
        await _artistRepository
            .SetExternalIdsAsync(dbId, new Dictionary<string, string> { [SourceId] = slug }, ct)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "RaScrapeAdapter: resolved artist '{Name}' → RA slug='{Slug}'.", artist.Name, slug);

        return slug;
    }

    private RawEvent? MapToRawEvent(JsonNode ev, string fallbackArtistName)
    {
        string? idRaw = ev["id"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(idRaw))
        {
            _logger.LogDebug("RaScrapeAdapter: event has no id; skipping.");
            return null;
        }

        // Build SourceUrl from contentUrl (a path like /events/1234567).
        string? contentUrl = ev["contentUrl"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(contentUrl))
        {
            _logger.LogDebug("RaScrapeAdapter: event {Id} has no contentUrl; skipping.", idRaw);
            return null;
        }

        string sourceUrl = contentUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? contentUrl
            : $"https://ra.co{contentUrl}";

        // Parse event date and startTime.
        string? dateStr      = ev["date"]?.GetValue<string>();
        string? startTimeStr = ev["startTime"]?.GetValue<string>();

        if (string.IsNullOrWhiteSpace(dateStr))
        {
            _logger.LogDebug("RaScrapeAdapter: event {Id} has no date; skipping.", idRaw);
            return null;
        }

        DateTimeOffset eventDt;
        if (!string.IsNullOrWhiteSpace(startTimeStr) &&
            DateTimeOffset.TryParse($"{dateStr}T{startTimeStr}", out var combined))
        {
            eventDt = combined;
        }
        else if (DateTimeOffset.TryParse(dateStr, out var dateParsed))
        {
            eventDt = dateParsed;
        }
        else
        {
            _logger.LogDebug(
                "RaScrapeAdapter: could not parse date '{Date}' for event {Id}; skipping.",
                dateStr, idRaw);
            return null;
        }

        // Venue fields.
        var venue        = ev["venue"];
        string? venueName    = venue?["name"]?.GetValue<string>();
        string? venueAddress = venue?["address"]?.GetValue<string>();
        var area             = venue?["area"];
        string? city         = area?["name"]?.GetValue<string>();
        string? country      = area?["country"]?["name"]?.GetValue<string>();

        // Lineup from artists array.
        var lineup = new List<string>();
        if (ev["artists"] is JsonArray artistsArr)
        {
            foreach (var a in artistsArr)
            {
                string? name = a?["name"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(name))
                    lineup.Add(name);
            }
        }

        string artistName = lineup.Count > 0 ? lineup[0] : fallbackArtistName;

        // TODO: RA GraphQL response does not expose price ranges or sold-out flags.
        // Update this mapping if RA adds those fields to the eventListings schema.

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

    /// <summary>
    /// Posts a GraphQL request and returns the parsed JSON response root node.
    /// </summary>
    private async Task<JsonNode?> PostGraphQlAsync(
        string query,
        object variables,
        CancellationToken ct)
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
            client.DefaultRequestHeaders.Referrer = new Uri("https://ra.co/events");

            var payload = new { query, variables };
            using var requestContent = JsonContent.Create(payload, options: JsonOptions);

            HttpResponseMessage response;
            try
            {
                response = await client.PostAsync(GraphQlUrl, requestContent, ct)
                    .ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex,
                    "RaScrapeAdapter: network error on attempt {Attempt}.", attempt + 1);
                lastEx = ex;
                continue;
            }

            await _rateLimiter.RecordCallAsync(SourceId, ct).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                _logger.LogWarning(
                    "RaScrapeAdapter: received 403 Forbidden from RA GraphQL. " +
                    "Aborting without retry.");
                return null;
            }

            if (response.IsSuccessStatusCode)
            {
                string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                await _sourceStateRepository
                    .RecordSuccessAsync(SourceId, DateTimeOffset.UtcNow, ct)
                    .ConfigureAwait(false);
                return JsonNode.Parse(body);
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests ||
                (int)response.StatusCode >= 500)
            {
                if (response.Headers.RetryAfter is { } retryAfter)
                {
                    DateTimeOffset until = retryAfter.Date
                        ?? DateTimeOffset.UtcNow.Add(retryAfter.Delta ?? TimeSpan.FromSeconds(60));
                    await _rateLimiter.SetBackoffAsync(SourceId, until, ct).ConfigureAwait(false);
                }

                lastEx = new HttpRequestException(
                    $"RA GraphQL returned {(int)response.StatusCode}.",
                    null,
                    response.StatusCode);
                _logger.LogWarning(
                    "RaScrapeAdapter: transient error {Status} on attempt {Attempt}.",
                    response.StatusCode, attempt + 1);
                continue;
            }

            // Non-transient error — fail immediately.
            response.EnsureSuccessStatusCode();
        }

        throw new HttpRequestException(
            $"RA GraphQL request failed after {RetryDelays.Length + 1} attempts.",
            lastEx);
    }
}
