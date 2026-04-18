// TODO [T7.3][FE]: Attribution requirement — EdmTrain ToS mandates that the UI renders a
// "Data: EdmTrain" attribution badge wherever EdmTrain data is displayed. The FE agent must:
//   • Show a "Data: EdmTrain" badge on the admin status card when this source is enabled.
//   • Show a "Data: EdmTrain" badge on each EdmTrain row in the user concert view.
// This is non-blocking for v0 ship but is required before ToS compliance.

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
/// Source adapter that fetches upcoming events from the EdmTrain public API.
/// EdmTrain covers electronic music events and festivals.
/// Attribution is required in the UI whenever this source is enabled — see TODO above.
/// </summary>
public sealed class EdmTrainAdapter : ISourceAdapter
{
    private const string SourceId = "edmtrain";
    private const string BaseUrl = "https://edmtrain.com/api";

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
    private readonly ArtistRepository _artistRepository;
    private readonly SourceStateRepository _sourceStateRepository;
    private readonly ILogger<EdmTrainAdapter> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="EdmTrainAdapter"/> class.
    /// </summary>
    /// <param name="httpClientFactory">HTTP client factory.</param>
    /// <param name="rateLimiter">Shared rate limiter.</param>
    /// <param name="artistRepository">Artist repository for caching external IDs.</param>
    /// <param name="sourceStateRepository">Source state repository for circuit breaker.</param>
    /// <param name="logger">Logger.</param>
    public EdmTrainAdapter(
        IHttpClientFactory httpClientFactory,
        HostRateLimiter rateLimiter,
        ArtistRepository artistRepository,
        SourceStateRepository sourceStateRepository,
        ILogger<EdmTrainAdapter> logger)
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
    public string DisplayName => "EdmTrain";

    /// <inheritdoc />
    public SourceKind Kind => SourceKind.Api;

    /// <inheritdoc />
    public bool RequiresCredentials => true;

    /// <inheritdoc />
    public bool RequiresTosOptIn => false;

    /// <inheritdoc />
    public bool IsConfigured(PluginConfiguration cfg)
        => !string.IsNullOrWhiteSpace(cfg.EdmTrainApiKey);

    /// <inheritdoc />
    public async IAsyncEnumerable<RawEvent> FetchAsync(
        ArtistRef artist,
        SourceFilter filter,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null || !IsConfigured(cfg))
            yield break;

        string apiKey = cfg.EdmTrainApiKey;

        // Step 1: resolve EdmTrain numeric artistId (cached in ext_ids under "edmtrain").
        string? artistId = artist.ExternalIds.TryGetValue(SourceId, out var cached)
            ? cached
            : null;

        if (string.IsNullOrEmpty(artistId))
        {
            artistId = await ResolveArtistIdAsync(artist, apiKey, ct).ConfigureAwait(false);
            if (artistId is null)
            {
                _logger.LogInformation(
                    "EdmTrainAdapter: no matching artist found on EdmTrain for '{Name}'. Skipping.",
                    artist.Name);
                yield break;
            }
        }

        // Step 2: fetch events for the resolved artistId.
        string startDate = filter.MinDate.UtcDateTime.ToString("yyyy-MM-dd");
        string endDate   = filter.MaxDate.UtcDateTime.ToString("yyyy-MM-dd");

        string url = $"{BaseUrl}/events?artistIds={Uri.EscapeDataString(artistId)}" +
                     $"&startDate={startDate}&endDate={endDate}" +
                     $"&client={Uri.EscapeDataString(apiKey)}";

        EdmResponse<EdmEvent>? resp;
        try
        {
            resp = await GetWithRetryAsync<EdmResponse<EdmEvent>>(url, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "EdmTrainAdapter: error fetching events for '{Artist}'.", artist.Name);
            throw;
        }

        if (resp?.Data is null)
            yield break;

        foreach (var ev in resp.Data)
        {
            ct.ThrowIfCancellationRequested();
            var raw = MapToRawEvent(ev, artist.Name);
            if (raw is not null)
                yield return raw;
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Resolves the EdmTrain numeric artist ID by name search. The result is persisted in
    /// <see cref="ArtistRepository"/> so subsequent runs skip the lookup.
    /// </summary>
    private async Task<string?> ResolveArtistIdAsync(ArtistRef artist, string apiKey, CancellationToken ct)
    {
        string encoded = Uri.EscapeDataString(artist.Name);
        string url = $"{BaseUrl}/artists?name={encoded}&client={Uri.EscapeDataString(apiKey)}";

        EdmResponse<EdmArtist>? resp;
        try
        {
            resp = await GetWithRetryAsync<EdmResponse<EdmArtist>>(url, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "EdmTrainAdapter: error resolving artist ID for '{Name}'.", artist.Name);
            return null;
        }

        if (resp?.Data is null || resp.Data.Count == 0)
            return null;

        // Pick first exact case-insensitive name match.
        string? artistId = null;
        foreach (var a in resp.Data)
        {
            if (string.Equals(a.Name, artist.Name, StringComparison.OrdinalIgnoreCase))
            {
                artistId = a.Id.ToString();
                break;
            }
        }

        if (artistId is null)
        {
            _logger.LogInformation(
                "EdmTrainAdapter: no exact name match for '{Name}' in EdmTrain artist results.",
                artist.Name);
            return null;
        }

        // Persist for future runs.
        string dbId = ArtistRepository.ComputeId(artist.Mbid, artist.Name);
        await _artistRepository
            .SetExternalIdsAsync(dbId, new Dictionary<string, string> { [SourceId] = artistId }, ct)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "EdmTrainAdapter: resolved artist '{Name}' → EdmTrain artistId={Id}.",
            artist.Name, artistId);

        return artistId;
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
            client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);

            HttpResponseMessage response;
            try
            {
                response = await client.GetAsync(url, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex,
                    "EdmTrainAdapter: network error on attempt {Attempt}.", attempt + 1);
                lastEx = ex;
                continue;
            }

            await _rateLimiter.RecordCallAsync(SourceId, ct).ConfigureAwait(false);

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
                    $"EdmTrain returned {(int)response.StatusCode}.",
                    null,
                    response.StatusCode);
                _logger.LogWarning(
                    "EdmTrainAdapter: transient error {Status} on attempt {Attempt}.",
                    response.StatusCode, attempt + 1);
                continue;
            }

            // Non-transient error — fail immediately.
            response.EnsureSuccessStatusCode();
        }

        throw new HttpRequestException(
            $"EdmTrain request failed after {RetryDelays.Length + 1} attempts: {url}",
            lastEx);
    }

    private RawEvent? MapToRawEvent(EdmEvent ev, string fallbackArtistName)
    {
        // EdmTrain's "link" field is the canonical event URL. Construct a fallback if absent.
        string sourceUrl = !string.IsNullOrWhiteSpace(ev.Link)
            ? ev.Link
            : $"https://edmtrain.com/events/{ev.Id}";

        // EdmTrain returns date as "yyyy-MM-dd" (venue-local, no time component).
        // We parse as midnight with DateTimeKind.Unspecified and represent as DateTimeOffset
        // with zero offset as a placeholder. Consumers should treat this as local-time display.
        if (string.IsNullOrWhiteSpace(ev.Date))
        {
            _logger.LogDebug(
                "EdmTrainAdapter: skipping event {Id} — no date field.", ev.Id);
            return null;
        }

        DateTimeOffset eventDt;
        if (DateTime.TryParse(ev.Date, out var parsedDate))
        {
            // Store as midnight with zero UTC offset (unspecified local time).
            eventDt = new DateTimeOffset(parsedDate.Year, parsedDate.Month, parsedDate.Day,
                0, 0, 0, TimeSpan.Zero);
        }
        else
        {
            _logger.LogDebug(
                "EdmTrainAdapter: skipping event {Id} — unparseable date '{Date}'.", ev.Id, ev.Date);
            return null;
        }

        // Build lineup from artistList.
        var lineup = new List<string>();
        if (ev.ArtistList is not null)
        {
            foreach (var a in ev.ArtistList)
            {
                if (!string.IsNullOrWhiteSpace(a.Name))
                    lineup.Add(a.Name);
            }
        }

        string artistName = lineup.Count > 0 ? lineup[0] : fallbackArtistName;

        // Venue and location fields.
        // venue.location is a plain string (city name or "Virtual").
        // venue.state is the US state abbreviation when applicable.
        string? venueName    = ev.Venue?.Name;
        string? venueAddress = ev.Venue?.Address;
        string? city         = ev.Venue?.Location; // "location" is the city/area string
        string? region       = ev.Venue?.State;
        // EdmTrain does not return a country code — assume null (no signal in schema).
        // TODO: Derive country from state (US-only) if region is set and country resolution is needed.
        double? lat = ev.Venue?.Latitude;
        double? lon = ev.Venue?.Longitude;

        // IsSoldOut: no signal in EdmTrain schema.
        // TODO: Update if EdmTrain adds a sold-out indicator to the API response.
        bool isSoldOut = false;

        return new RawEvent(
            SourceEventId: ev.Id.ToString(),
            SourceUrl: sourceUrl,
            ArtistName: artistName,
            EventDateTime: eventDt,
            VenueName: venueName,
            VenueAddress: venueAddress,
            City: city,
            Region: region,
            Country: null,
            Lat: lat,
            Lon: lon,
            Lineup: lineup,
            TicketUrl: ev.TicketLink,
            PriceMin: null,
            PriceMax: null,
            Currency: null,
            OnSaleAt: null,
            IsFestival: ev.FestivalInd,
            IsSoldOut: isSoldOut);
    }

    // ── DTOs ──────────────────────────────────────────────────────────────────
    // Field names confirmed against the reference Kotlin client (kikkia/edmtrain-client
    // ParseUtils.kt) which parses the live EdmTrain API. JSON is case-insensitive.

    private sealed class EdmResponse<T>
    {
        public bool Success { get; set; }
        public List<T>? Data { get; set; }
    }

    private sealed class EdmEvent
    {
        public int Id { get; set; }

        /// <summary>Gets or sets the EdmTrain event page URL.</summary>
        public string? Link { get; set; }

        /// <summary>Gets or sets the external ticket purchase URL.</summary>
        public string? TicketLink { get; set; }

        /// <summary>Gets or sets the event name (may be null when the headliner name is used instead).</summary>
        public string? Name { get; set; }

        /// <summary>Gets or sets age restriction string, e.g. "21+" or "All Ages".</summary>
        public string? Ages { get; set; }

        /// <summary>Gets or sets a value indicating whether this is a festival event.</summary>
        [JsonPropertyName("festivalInd")]
        public bool FestivalInd { get; set; }

        /// <summary>Gets or sets a value indicating whether this is an electronic music event.</summary>
        [JsonPropertyName("electronicGenreInd")]
        public bool ElectronicGenreInd { get; set; }

        /// <summary>Gets or sets a value indicating whether this is a non-electronic event.</summary>
        [JsonPropertyName("otherGenreInd")]
        public bool OtherGenreInd { get; set; }

        /// <summary>Gets or sets the event date in "yyyy-MM-dd" format (venue local).</summary>
        public string? Date { get; set; }

        public EdmVenue? Venue { get; set; }

        /// <summary>Gets or sets the list of performing artists.</summary>
        public List<EdmArtist>? ArtistList { get; set; }
    }

    private sealed class EdmVenue
    {
        public int Id { get; set; }
        public string? Name { get; set; }

        /// <summary>Gets or sets the city/area string (e.g. "Los Angeles" or "Virtual").</summary>
        public string? Location { get; set; }

        public string? Address { get; set; }

        /// <summary>Gets or sets the US state abbreviation (e.g. "CA"), or empty for non-US venues.</summary>
        public string? State { get; set; }

        public double? Latitude { get; set; }
        public double? Longitude { get; set; }
    }

    private sealed class EdmArtist
    {
        public int Id { get; set; }
        public string? Name { get; set; }
        /// <summary>Gets or sets the artist's EdmTrain profile link.</summary>
        public string? Link { get; set; }
    }
}
