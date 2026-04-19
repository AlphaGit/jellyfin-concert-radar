using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
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
/// Source adapter that fetches upcoming events from the Bandsintown public API.
/// Bandsintown's events endpoint returns all upcoming events for an artist regardless of
/// location — client-side filtering is applied by <c>Normalizer</c> via <see cref="SourceFilter"/>.
/// </summary>
public sealed class BandsintownAdapter : ISourceAdapter
{
    private const string SourceId = "bandsintown";
    private const string BaseUrl = "https://rest.bandsintown.com";

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

    // TODO: derive version from Plugin.Instance.Version when available.
    private static readonly string UserAgent =
        "JellyfinConcertRadar/0.1.0 ( https://github.com/alphagma/jellyfin-concert-radar )";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly HostRateLimiter _rateLimiter;
    private readonly SourceStateRepository _sourceStateRepository;
    private readonly IPluginConfigurationProvider _configProvider;
    private readonly TimeProvider _clock;
    private readonly ILogger<BandsintownAdapter> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="BandsintownAdapter"/> class.
    /// </summary>
    /// <param name="httpClientFactory">HTTP client factory.</param>
    /// <param name="rateLimiter">Shared rate limiter.</param>
    /// <param name="sourceStateRepository">Source state repository for circuit breaker.</param>
    /// <param name="configProvider">Plugin configuration provider.</param>
    /// <param name="clock">Time provider.</param>
    /// <param name="logger">Logger.</param>
    public BandsintownAdapter(
        IHttpClientFactory httpClientFactory,
        HostRateLimiter rateLimiter,
        SourceStateRepository sourceStateRepository,
        IPluginConfigurationProvider configProvider,
        TimeProvider clock,
        ILogger<BandsintownAdapter> logger)
    {
        _httpClientFactory     = httpClientFactory;
        _rateLimiter           = rateLimiter;
        _sourceStateRepository = sourceStateRepository;
        _configProvider        = configProvider;
        _clock                 = clock;
        _logger                = logger;
    }

    /// <inheritdoc />
    public string Id => SourceId;

    /// <inheritdoc />
    public string DisplayName => "Bandsintown";

    /// <inheritdoc />
    public SourceKind Kind => SourceKind.Api;

    /// <inheritdoc />
    public bool RequiresCredentials => true;

    /// <inheritdoc />
    public bool RequiresTosOptIn => false;

    /// <inheritdoc />
    public bool IsConfigured(PluginConfiguration cfg)
        => !string.IsNullOrWhiteSpace(cfg.BandsintownAppId);

    /// <inheritdoc />
    public async IAsyncEnumerable<RawEvent> FetchAsync(
        ArtistRef artist,
        SourceFilter filter,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var cfg = _configProvider.GetConfiguration();
        if (cfg is null || !IsConfigured(cfg))
            yield break;

        string appId = cfg.BandsintownAppId;

        // Resolve the artist identifier used in the BIT URL path.
        // Priority order:
        //   1. ext_ids["bandsintown"] — already prefixed ("mbid_<uuid>") by MusicBrainzResolver.
        //   2. If Mbid is present, synthesize "mbid_<uuid>".
        //   3. Fall back to URL-encoded artist name.
        string identifier;
        if (artist.ExternalIds.TryGetValue(SourceId, out var cachedId) &&
            !string.IsNullOrWhiteSpace(cachedId))
        {
            identifier = cachedId;
        }
        else if (!string.IsNullOrWhiteSpace(artist.Mbid))
        {
            identifier = "mbid_" + artist.Mbid;
        }
        else
        {
            identifier = Uri.EscapeDataString(artist.Name);
        }

        string url = $"{BaseUrl}/artists/{identifier}/events?app_id={Uri.EscapeDataString(appId)}";

        BitEvent[]? events;
        try
        {
            events = await GetWithRetryAsync<BitEvent[]>(url, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "BandsintownAdapter: error fetching events for '{Artist}'.", artist.Name);
            throw;
        }

        if (events is null)
            yield break;

        foreach (var ev in events)
        {
            var raw = MapToRawEvent(ev, artist.Name);
            if (raw is not null)
                yield return raw;
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

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
            client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);

            HttpResponseMessage response;
            try
            {
                response = await client.GetAsync(url, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex, "BandsintownAdapter: network error on attempt {Attempt}.", attempt + 1);
                lastEx = ex;
                continue;
            }

            // 404 = artist not found on Bandsintown — not an error, yield nothing.
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                _logger.LogInformation(
                    "BandsintownAdapter: artist not found on Bandsintown (404) for URL {Url}.", url);
                return null;
            }

            await _rateLimiter.RecordCallAsync(SourceId, ct).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                return JsonSerializer.Deserialize<T>(body, JsonOptions);
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests ||
                (int)response.StatusCode >= 500)
            {
                // Honor Retry-After.
                if (response.Headers.RetryAfter is { } retryAfter)
                {
                    DateTimeOffset until = retryAfter.Date
                        ?? _clock.GetUtcNow().Add(retryAfter.Delta ?? TimeSpan.FromSeconds(60));
                    await _rateLimiter.SetBackoffAsync(SourceId, until, ct).ConfigureAwait(false);
                }

                lastEx = new HttpRequestException(
                    $"Bandsintown returned {(int)response.StatusCode}.",
                    null,
                    response.StatusCode);
                _logger.LogWarning(
                    "BandsintownAdapter: transient error {Status} on attempt {Attempt}.",
                    response.StatusCode, attempt + 1);
                continue;
            }

            // Non-transient error — fail immediately.
            response.EnsureSuccessStatusCode();
        }

        throw new HttpRequestException(
            $"Bandsintown request failed after {RetryDelays.Length + 1} attempts: {url}",
            lastEx);
    }

    private RawEvent? MapToRawEvent(BitEvent ev, string fallbackArtistName)
    {
        if (string.IsNullOrWhiteSpace(ev.Url))
            return null;

        // Bandsintown's `datetime` field is ISO-8601 but without a timezone offset —
        // it represents venue-local time. We parse it as DateTimeKind.Unspecified and
        // store it with an unspecified offset (DateTimeOffset with zero-offset placeholder).
        // Consumers should display it as-is rather than converting to server TZ.
        DateTimeOffset eventDt;
        if (!string.IsNullOrEmpty(ev.DateTime) &&
            DateTimeOffset.TryParse(ev.DateTime, out var parsedDt))
        {
            eventDt = parsedDt;
        }
        else
        {
            _logger.LogDebug(
                "BandsintownAdapter: skipping event {Id} — no parseable datetime.", ev.Id);
            return null;
        }

        // Determine ticket URL and sold-out flag from the offers array.
        string? ticketUrl = null;
        bool isSoldOut = false;
        if (ev.Offers is not null)
        {
            foreach (var offer in ev.Offers)
            {
                if (!string.Equals(offer.Type, "Tickets", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (string.Equals(offer.Status, "available", StringComparison.OrdinalIgnoreCase))
                {
                    ticketUrl = offer.Url;
                    break;
                }

                if (string.Equals(offer.Status, "sold out", StringComparison.OrdinalIgnoreCase))
                {
                    isSoldOut = true;
                    ticketUrl = offer.Url; // Still capture the URL for display even if sold out.
                }
            }
        }

        // Fall back to the event URL when no dedicated ticket offer was found.
        ticketUrl ??= ev.Url;

        // Festival heuristic: no dedicated field exists on BIT events; check the title.
        bool isFestival = !string.IsNullOrEmpty(ev.Title) &&
            ev.Title.Contains("festival", StringComparison.OrdinalIgnoreCase);

        var lineup = ev.Lineup is not null
            ? (IReadOnlyList<string>)ev.Lineup
            : Array.Empty<string>();

        string artistName = lineup.Count > 0 ? lineup[0] : fallbackArtistName;

        double? lat = null;
        double? lon = null;
        if (!string.IsNullOrEmpty(ev.Venue?.Latitude) &&
            double.TryParse(ev.Venue.Latitude, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var parsedLat))
        {
            lat = parsedLat;
        }

        if (!string.IsNullOrEmpty(ev.Venue?.Longitude) &&
            double.TryParse(ev.Venue.Longitude, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var parsedLon))
        {
            lon = parsedLon;
        }

        return new RawEvent(
            SourceEventId: ev.Id ?? string.Empty,
            SourceUrl: ev.Url!,
            ArtistName: artistName,
            EventDateTime: eventDt,
            VenueName: ev.Venue?.Name,
            VenueAddress: ev.Venue?.Address,
            City: ev.Venue?.City,
            Region: ev.Venue?.Region,
            Country: ev.Venue?.Country,
            Lat: lat,
            Lon: lon,
            Lineup: lineup,
            TicketUrl: ticketUrl,
            PriceMin: null,
            PriceMax: null,
            Currency: null,
            OnSaleAt: null,
            IsFestival: isFestival,
            IsSoldOut: isSoldOut);
    }

    // ── DTOs ──────────────────────────────────────────────────────────────────

    private sealed class BitEvent
    {
        public string? Id { get; set; }

        public string? Url { get; set; }

        /// <summary>Gets or sets the event date/time in venue-local time (no TZ offset).</summary>
        [JsonPropertyName("datetime")]
        public string? DateTime { get; set; }

        public string? Title { get; set; }

        public BitVenue? Venue { get; set; }

        public List<string>? Lineup { get; set; }

        public List<BitOffer>? Offers { get; set; }
    }

    private sealed class BitVenue
    {
        public string? Name { get; set; }

        [JsonPropertyName("address1")]
        public string? Address { get; set; }

        public string? City { get; set; }

        public string? Region { get; set; }

        public string? Country { get; set; }

        public string? Latitude { get; set; }

        public string? Longitude { get; set; }
    }

    private sealed class BitOffer
    {
        public string? Type { get; set; }

        public string? Url { get; set; }

        public string? Status { get; set; }
    }
}
