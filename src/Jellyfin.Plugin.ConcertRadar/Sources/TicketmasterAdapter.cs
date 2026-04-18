using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
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
/// Source adapter that fetches upcoming events from the Ticketmaster Discovery API v2.
/// </summary>
public sealed class TicketmasterAdapter : ISourceAdapter
{
    private const string SourceId = "ticketmaster";
    private const string BaseUrl = "https://app.ticketmaster.com/discovery/v2";
    private const int MaxPages = 10;
    private const int PageSize = 200;

    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromMilliseconds(200),
        TimeSpan.FromMilliseconds(800),
        TimeSpan.FromMilliseconds(3200),
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly HostRateLimiter _rateLimiter;
    private readonly ArtistRepository _artistRepository;
    private readonly SourceStateRepository _sourceStateRepository;
    private readonly ILogger<TicketmasterAdapter> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TicketmasterAdapter"/> class.
    /// </summary>
    /// <param name="httpClientFactory">HTTP client factory.</param>
    /// <param name="rateLimiter">Shared rate limiter.</param>
    /// <param name="artistRepository">Artist repository for caching external IDs.</param>
    /// <param name="sourceStateRepository">Source state repository for circuit breaker.</param>
    /// <param name="logger">Logger.</param>
    public TicketmasterAdapter(
        IHttpClientFactory httpClientFactory,
        HostRateLimiter rateLimiter,
        ArtistRepository artistRepository,
        SourceStateRepository sourceStateRepository,
        ILogger<TicketmasterAdapter> logger)
    {
        _httpClientFactory      = httpClientFactory;
        _rateLimiter            = rateLimiter;
        _artistRepository       = artistRepository;
        _sourceStateRepository  = sourceStateRepository;
        _logger                 = logger;
    }

    /// <inheritdoc />
    public string Id => SourceId;

    /// <inheritdoc />
    public string DisplayName => "Ticketmaster";

    /// <inheritdoc />
    public SourceKind Kind => SourceKind.Api;

    /// <inheritdoc />
    public bool RequiresCredentials => true;

    /// <inheritdoc />
    public bool RequiresTosOptIn => false;

    /// <inheritdoc />
    public bool IsConfigured(PluginConfiguration cfg)
        => !string.IsNullOrWhiteSpace(cfg.TicketmasterApiKey);

    /// <inheritdoc />
    public async IAsyncEnumerable<RawEvent> FetchAsync(
        ArtistRef artist,
        SourceFilter filter,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null || !IsConfigured(cfg))
            yield break;

        string apiKey = cfg.TicketmasterApiKey;

        // Step 1: resolve attractionId (cached in ext_ids, else search by name).
        string? attractionId = artist.ExternalIds.TryGetValue(SourceId, out var cached) ? cached : null;

        if (string.IsNullOrEmpty(attractionId))
        {
            attractionId = await ResolveAttractionIdAsync(artist, apiKey, ct).ConfigureAwait(false);
            if (attractionId is null)
            {
                _logger.LogWarning("TicketmasterAdapter: could not resolve attraction ID for '{Name}'.", artist.Name);
                yield break;
            }
        }

        // Step 2: paginate events.
        int pageNum = 0;
        int totalPages = 1;

        while (pageNum < totalPages && pageNum < MaxPages)
        {
            ct.ThrowIfCancellationRequested();

            TmEventsResponse? page;
            try
            {
                page = await FetchEventsPageAsync(attractionId, filter, apiKey, pageNum, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "TicketmasterAdapter: error fetching page {Page} for '{Artist}'.", pageNum, artist.Name);
                throw;
            }

            if (page is null)
                yield break;

            totalPages = Math.Min(page.Page?.TotalPages ?? 1, MaxPages);

            if (page.Embedded?.Events is null)
                break;

            foreach (var ev in page.Embedded.Events)
            {
                var raw = MapToRawEvent(ev, artist.Name);
                if (raw is not null)
                    yield return raw;
            }

            pageNum++;
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task<string?> ResolveAttractionIdAsync(ArtistRef artist, string apiKey, CancellationToken ct)
    {
        string encoded = Uri.EscapeDataString(artist.Name);
        string url = $"{BaseUrl}/attractions.json?keyword={encoded}&apikey={apiKey}&size=5";

        TmAttractionsResponse? resp = await GetWithRetryAsync<TmAttractionsResponse>(url, ct)
            .ConfigureAwait(false);

        if (resp?.Embedded?.Attractions is null)
            return null;

        // Pick best match by exact case-insensitive name.
        string? attractionId = null;
        foreach (var att in resp.Embedded.Attractions)
        {
            if (string.Equals(att.Name, artist.Name, StringComparison.OrdinalIgnoreCase))
            {
                attractionId = att.Id;
                break;
            }
        }

        // Fall back to first result if no exact match.
        attractionId ??= resp.Embedded.Attractions.Count > 0
            ? resp.Embedded.Attractions[0].Id
            : null;

        if (attractionId is not null)
        {
            // Derive the DB primary key using the same logic as ArtistRepository.ComputeId.
            string dbId = ArtistRepository.ComputeId(artist.Mbid, artist.Name);
            await _artistRepository
                .SetExternalIdsAsync(dbId, new Dictionary<string, string> { [SourceId] = attractionId }, ct)
                .ConfigureAwait(false);
        }

        return attractionId;
    }

    private async Task<TmEventsResponse?> FetchEventsPageAsync(
        string attractionId,
        SourceFilter filter,
        string apiKey,
        int page,
        CancellationToken ct)
    {
        var qb = new List<string>
        {
            $"attractionId={Uri.EscapeDataString(attractionId)}",
            $"apikey={apiKey}",
            $"size={PageSize}",
            $"page={page}",
            $"startDateTime={filter.MinDate.UtcDateTime:yyyy-MM-ddTHH:mm:ss'Z'}",
            $"endDateTime={filter.MaxDate.UtcDateTime:yyyy-MM-ddTHH:mm:ss'Z'}",
        };

        if (filter.CountryAllowlist.Count > 0)
            qb.Add($"countryCode={Uri.EscapeDataString(string.Join(",", filter.CountryAllowlist))}");

        // TODO: map GenreAllowlist to classificationId when Ticketmaster classification ids are known.

        string url = $"{BaseUrl}/events.json?{string.Join("&", qb)}";

        return await GetWithRetryAsync<TmEventsResponse>(url, ct).ConfigureAwait(false);
    }

    private async Task<T?> GetWithRetryAsync<T>(string url, CancellationToken ct) where T : class
    {
        Exception? lastEx = null;

        for (int attempt = 0; attempt <= RetryDelays.Length; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            if (attempt > 0)
                await Task.Delay(RetryDelays[attempt - 1], ct).ConfigureAwait(false);

            await _rateLimiter.AcquireAsync(SourceId, ct).ConfigureAwait(false);

            using var client = _httpClientFactory.CreateClient(NamedClient.Default);

            HttpResponseMessage response;
            try
            {
                response = await client.GetAsync(url, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex, "TicketmasterAdapter: network error on attempt {Attempt}.", attempt + 1);
                lastEx = ex;
                continue;
            }

            await _rateLimiter.RecordCallAsync(SourceId, ct).ConfigureAwait(false);

            // Log Rate-Limit-* headers.
            if (response.Headers.TryGetValues("Rate-Limit-Available", out var avail))
                _logger.LogInformation("Ticketmaster quota remaining: {Available}.", string.Join(",", avail));
            if (response.Headers.TryGetValues("Rate-Limit-Over", out var over))
                _logger.LogWarning("Ticketmaster Rate-Limit-Over: {Over}.", string.Join(",", over));

            if (response.IsSuccessStatusCode)
            {
                string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                await _sourceStateRepository.RecordSuccessAsync(SourceId, DateTimeOffset.UtcNow, ct)
                    .ConfigureAwait(false);
                return JsonSerializer.Deserialize<T>(body, JsonOptions);
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests ||
                (int)response.StatusCode >= 500)
            {
                // Honor Retry-After.
                if (response.Headers.RetryAfter is { } retryAfter)
                {
                    DateTimeOffset until = retryAfter.Date
                        ?? DateTimeOffset.UtcNow.Add(retryAfter.Delta ?? TimeSpan.FromSeconds(60));
                    await _rateLimiter.SetBackoffAsync(SourceId, until, ct).ConfigureAwait(false);
                }

                lastEx = new HttpRequestException(
                    $"Ticketmaster returned {(int)response.StatusCode}.",
                    null,
                    response.StatusCode);
                _logger.LogWarning(
                    "TicketmasterAdapter: transient error {Status} on attempt {Attempt}.",
                    response.StatusCode, attempt + 1);
                continue;
            }

            // Non-transient: fail immediately.
            response.EnsureSuccessStatusCode();
        }

        throw new HttpRequestException(
            $"Ticketmaster request failed after {RetryDelays.Length + 1} attempts: {url}",
            lastEx);
    }

    private RawEvent? MapToRawEvent(TmEvent ev, string fallbackArtistName)
    {
        if (string.IsNullOrWhiteSpace(ev.Url))
            return null;

        // Parse event date/time.
        DateTimeOffset eventDt;
        if (!string.IsNullOrEmpty(ev.Dates?.Start?.DateTime) &&
            DateTimeOffset.TryParse(ev.Dates.Start.DateTime, out var parsedDt))
        {
            eventDt = parsedDt;
        }
        else if (!string.IsNullOrEmpty(ev.Dates?.Start?.LocalDate) &&
                 DateTimeOffset.TryParse(ev.Dates.Start.LocalDate, out var localDt))
        {
            eventDt = localDt;
        }
        else
        {
            _logger.LogDebug("TicketmasterAdapter: skipping event {Id} — no parseable date.", ev.Id);
            return null;
        }

        var venue = ev.Embedded?.Venues?.Count > 0 ? ev.Embedded.Venues[0] : null;

        // Build lineup from embedded attractions.
        var lineup = new List<string>();
        if (ev.Embedded?.Attractions is not null)
        {
            foreach (var att in ev.Embedded.Attractions)
            {
                if (!string.IsNullOrWhiteSpace(att.Name))
                    lineup.Add(att.Name);
            }
        }

        string artistName = lineup.Count > 0 ? lineup[0] : fallbackArtistName;

        // Price range.
        decimal? priceMin = null;
        decimal? priceMax = null;
        string? currency = null;
        if (ev.PriceRanges?.Count > 0)
        {
            var pr = ev.PriceRanges[0];
            if (pr.Min.HasValue) priceMin = (decimal)pr.Min.Value;
            if (pr.Max.HasValue) priceMax = (decimal)pr.Max.Value;
            currency = pr.Currency;
        }

        // On-sale date.
        DateTimeOffset? onSaleAt = null;
        if (!string.IsNullOrEmpty(ev.Sales?.Public?.StartDateTime) &&
            DateTimeOffset.TryParse(ev.Sales.Public.StartDateTime, out var saleDt))
        {
            onSaleAt = saleDt;
        }

        // Festival flag.
        bool isFestival = false;
        if (ev.Classifications?.Count > 0)
        {
            var cls = ev.Classifications[0];
            isFestival = string.Equals(cls.Genre?.Name, "Festival", StringComparison.OrdinalIgnoreCase);
        }

        // Cancelled/postponed → treat as not available; no direct sold-out flag.
        // TODO: Ticketmaster has no direct sold-out flag in the Discovery API response.
        bool isSoldOut = false;
        string? statusCode = ev.Dates?.Status?.Code;
        if (string.Equals(statusCode, "cancelled", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(statusCode, "postponed", StringComparison.OrdinalIgnoreCase))
        {
            isSoldOut = false; // not sold out, just unavailable — consumers may skip via status
        }

        double? lat = null;
        double? lon = null;
        if (venue?.Location is not null)
        {
            if (double.TryParse(venue.Location.Latitude, out double parsedLat)) lat = parsedLat;
            if (double.TryParse(venue.Location.Longitude, out double parsedLon)) lon = parsedLon;
        }

        return new RawEvent(
            SourceEventId: ev.Id ?? string.Empty,
            SourceUrl: ev.Url,
            ArtistName: artistName,
            EventDateTime: eventDt,
            VenueName: venue?.Name,
            VenueAddress: venue?.Address?.Line1,
            City: venue?.City?.Name,
            Region: venue?.State?.Name,
            Country: venue?.Country?.CountryCode,
            Lat: lat,
            Lon: lon,
            Lineup: lineup,
            TicketUrl: ev.Url,
            PriceMin: priceMin,
            PriceMax: priceMax,
            Currency: currency,
            OnSaleAt: onSaleAt,
            IsFestival: isFestival,
            IsSoldOut: isSoldOut);
    }

    // ── DTOs ──────────────────────────────────────────────────────────────────

    private sealed class TmAttractionsResponse
    {
        [JsonPropertyName("_embedded")]
        public TmAttractionsEmbedded? Embedded { get; set; }
    }

    private sealed class TmAttractionsEmbedded
    {
        public List<TmAttraction>? Attractions { get; set; }
    }

    private sealed class TmAttraction
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
    }

    private sealed class TmEventsResponse
    {
        [JsonPropertyName("_embedded")]
        public TmEventsEmbedded? Embedded { get; set; }

        public TmPage? Page { get; set; }
    }

    private sealed class TmEventsEmbedded
    {
        public List<TmEvent>? Events { get; set; }
    }

    private sealed class TmPage
    {
        public int TotalPages { get; set; }
        public int TotalElements { get; set; }
        public int Number { get; set; }
        public int Size { get; set; }
    }

    private sealed class TmEvent
    {
        public string? Id { get; set; }
        public string? Url { get; set; }
        public TmDates? Dates { get; set; }
        public TmSales? Sales { get; set; }
        public List<TmPriceRange>? PriceRanges { get; set; }
        public List<TmClassification>? Classifications { get; set; }

        [JsonPropertyName("_embedded")]
        public TmEventEmbedded? Embedded { get; set; }
    }

    private sealed class TmEventEmbedded
    {
        public List<TmVenue>? Venues { get; set; }
        public List<TmAttraction>? Attractions { get; set; }
    }

    private sealed class TmDates
    {
        public TmStart? Start { get; set; }
        public TmStatus? Status { get; set; }
    }

    private sealed class TmStart
    {
        public string? LocalDate { get; set; }
        public string? DateTime { get; set; }
    }

    private sealed class TmStatus
    {
        public string? Code { get; set; }
    }

    private sealed class TmSales
    {
        public TmPublicSale? Public { get; set; }
    }

    private sealed class TmPublicSale
    {
        public string? StartDateTime { get; set; }
    }

    private sealed class TmPriceRange
    {
        public string? Type { get; set; }
        public string? Currency { get; set; }
        public double? Min { get; set; }
        public double? Max { get; set; }
    }

    private sealed class TmClassification
    {
        public TmGenre? Genre { get; set; }
    }

    private sealed class TmGenre
    {
        public string? Name { get; set; }
    }

    private sealed class TmVenue
    {
        public string? Name { get; set; }
        public TmAddress? Address { get; set; }
        public TmCity? City { get; set; }
        public TmState? State { get; set; }
        public TmCountry? Country { get; set; }
        public TmLocation? Location { get; set; }
    }

    private sealed class TmAddress
    {
        public string? Line1 { get; set; }
    }

    private sealed class TmCity
    {
        public string? Name { get; set; }
    }

    private sealed class TmState
    {
        public string? Name { get; set; }
    }

    private sealed class TmCountry
    {
        public string? CountryCode { get; set; }
        public string? Name { get; set; }
    }

    private sealed class TmLocation
    {
        public string? Latitude { get; set; }
        public string? Longitude { get; set; }
    }
}
