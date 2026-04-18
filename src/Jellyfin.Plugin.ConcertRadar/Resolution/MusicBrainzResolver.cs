using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ConcertRadar.RateLimiting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ConcertRadar.Resolution;

/// <summary>
/// Resolves MusicBrainz artist IDs and external source IDs (Songkick, Bandsintown, RA, Dice)
/// via the MusicBrainz JSON web-service.
/// </summary>
public sealed class MusicBrainzResolver
{
    private const string SourceKey = "musicbrainz";
    // Score threshold below which name-based matches are rejected.
    private const int MinScoreThreshold = 85;

    // Retry delays: 200ms, 800ms, 3200ms (3 attempts = 3 waits after initial failure).
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromMilliseconds(200),
        TimeSpan.FromMilliseconds(800),
        TimeSpan.FromMilliseconds(3200),
    ];

    private static readonly Regex SongkickIdRegex =
        new(@"/artists/(\d+)", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    private static readonly Regex RaSlugRegex =
        new(@"ra\.co/dj/([^/?#]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));

    private static readonly Regex DiceSlugRegex =
        new(@"dice\.fm/artist/([^/?#]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly HostRateLimiter _rateLimiter;
    private readonly ILogger<MusicBrainzResolver> _logger;

    // TODO: derive version from Plugin.Instance.Version when available.
    private static readonly string UserAgent =
        "JellyfinConcertRadar/0.1.0 ( https://github.com/alphagma/jellyfin-concert-radar )";

    /// <summary>
    /// Initializes a new instance of the <see cref="MusicBrainzResolver"/> class.
    /// </summary>
    /// <param name="httpClientFactory">Factory for creating HTTP clients.</param>
    /// <param name="rateLimiter">Shared rate limiter; uses the "musicbrainz" key.</param>
    /// <param name="logger">Logger.</param>
    public MusicBrainzResolver(
        IHttpClientFactory httpClientFactory,
        HostRateLimiter rateLimiter,
        ILogger<MusicBrainzResolver> logger)
    {
        _httpClientFactory = httpClientFactory;
        _rateLimiter       = rateLimiter;
        _logger            = logger;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Searches MusicBrainz for an artist by name and returns the MBID of the top-scoring
    /// match, or <c>null</c> if no match scores ≥85.
    /// </summary>
    public async Task<string?> ResolveMbidByNameAsync(string name, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var url = $"https://musicbrainz.org/ws/2/artist?query=artist:%22{Uri.EscapeDataString(name)}%22&fmt=json";
        var body = await GetWithRetryAsync(url, ct).ConfigureAwait(false);

        using var doc = JsonDocument.Parse(body);
        var artists = doc.RootElement.GetProperty("artists");

        if (artists.GetArrayLength() == 0)
            return null;

        var top = artists[0];
        int score = top.TryGetProperty("score", out var scoreProp)
            ? scoreProp.GetInt32()
            : 0;

        if (score < MinScoreThreshold)
        {
            _logger.LogDebug(
                "MusicBrainz: top match for '{Name}' scored {Score} — below threshold {Threshold}.",
                name, score, MinScoreThreshold);
            return null;
        }

        return top.GetProperty("id").GetString();
    }

    /// <summary>
    /// Fetches URL relationships for <paramref name="mbid"/> from MusicBrainz and returns a
    /// dictionary mapping source keys to external IDs understood by each adapter.
    /// </summary>
    /// <returns>
    /// Dictionary with zero or more of the keys: <c>songkick</c>, <c>bandsintown</c>, <c>ra</c>, <c>dice</c>.
    /// </returns>
    public async Task<IReadOnlyDictionary<string, string>> FetchUrlRelsAsync(string mbid, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mbid);

        var url = $"https://musicbrainz.org/ws/2/artist/{Uri.EscapeDataString(mbid)}?inc=url-rels&fmt=json";
        var body = await GetWithRetryAsync(url, ct).ConfigureAwait(false);

        using var doc = JsonDocument.Parse(body);

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (!doc.RootElement.TryGetProperty("relations", out var relations))
            return result;

        foreach (var rel in relations.EnumerateArray())
        {
            if (!rel.TryGetProperty("type", out var typeProp)) continue;
            if (!rel.TryGetProperty("url", out var urlObj))    continue;
            if (!urlObj.TryGetProperty("resource", out var resourceProp)) continue;

            string type     = typeProp.GetString() ?? string.Empty;
            string resource = resourceProp.GetString() ?? string.Empty;

            ParseRelation(type, resource, mbid, result);
        }

        return result;
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static void ParseRelation(
        string type,
        string resource,
        string mbid,
        Dictionary<string, string> result)
    {
        // Songkick: type == "songkick" OR resource contains songkick.com/artists/<id>
        if (string.Equals(type, "songkick", StringComparison.OrdinalIgnoreCase) ||
            resource.Contains("songkick.com", StringComparison.OrdinalIgnoreCase))
        {
            var m = SongkickIdRegex.Match(resource);
            if (m.Success)
                result["songkick"] = m.Groups[1].Value;
            return;
        }

        // Bandsintown: BIT's canonical identifier is mbid_<uuid>, which we construct ourselves.
        // Store the mbid_ prefix form; the URL-rel is informational only.
        if (string.Equals(type, "bandsintown", StringComparison.OrdinalIgnoreCase) ||
            resource.Contains("bandsintown.com", StringComparison.OrdinalIgnoreCase))
        {
            result["bandsintown"] = $"mbid_{mbid}";
            return;
        }

        // Resident Advisor: type == "resident advisor" or URL matches ra.co/dj/<slug>
        if (string.Equals(type, "resident advisor", StringComparison.OrdinalIgnoreCase) ||
            resource.Contains("ra.co", StringComparison.OrdinalIgnoreCase))
        {
            var m = RaSlugRegex.Match(resource);
            if (m.Success)
                result["ra"] = m.Groups[1].Value;
            return;
        }

        // Dice.fm: type == "dice fm" or URL matches dice.fm/artist/<slug>
        if (string.Equals(type, "dice fm", StringComparison.OrdinalIgnoreCase) ||
            resource.Contains("dice.fm", StringComparison.OrdinalIgnoreCase))
        {
            var m = DiceSlugRegex.Match(resource);
            if (m.Success)
                result["dice"] = m.Groups[1].Value;
        }
    }

    /// <summary>
    /// GETs <paramref name="url"/> with rate limiting and retry on 429/5xx.
    /// Honors <c>Retry-After</c> header by calling <see cref="HostRateLimiter.SetBackoffAsync"/>.
    /// </summary>
    private async Task<string> GetWithRetryAsync(string url, CancellationToken ct)
    {
        Exception? lastEx = null;

        for (int attempt = 0; attempt <= RetryDelays.Length; attempt++)
        {
            if (attempt > 0)
            {
                await Task.Delay(RetryDelays[attempt - 1], ct).ConfigureAwait(false);
            }

            await _rateLimiter.AcquireAsync(SourceKey, ct).ConfigureAwait(false);

            using var client = _httpClientFactory.CreateClient(MediaBrowser.Common.Net.NamedClient.Default);
            client.DefaultRequestHeaders.UserAgent.Clear();
            client.DefaultRequestHeaders.Add("User-Agent", UserAgent);

            HttpResponseMessage response;
            try
            {
                response = await client.GetAsync(url, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex, "MusicBrainz: network error on attempt {Attempt} for {Url}.", attempt + 1, url);
                lastEx = ex;
                continue;
            }

            await _rateLimiter.RecordCallAsync(SourceKey, ct).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
                return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.TooManyRequests ||
                (int)response.StatusCode >= 500)
            {
                // Honor Retry-After when present.
                if (response.Headers.RetryAfter is { } retryAfter)
                {
                    DateTimeOffset until = retryAfter.Date
                        ?? DateTimeOffset.UtcNow.Add(retryAfter.Delta ?? TimeSpan.FromSeconds(60));
                    await _rateLimiter.SetBackoffAsync(SourceKey, until, ct).ConfigureAwait(false);
                }

                var statusEx = new HttpRequestException(
                    $"MusicBrainz returned {(int)response.StatusCode} for {url}.",
                    null,
                    response.StatusCode);
                _logger.LogWarning(statusEx,
                    "MusicBrainz: transient error {Status} on attempt {Attempt} for {Url}.",
                    response.StatusCode, attempt + 1, url);
                lastEx = statusEx;
                continue;
            }

            // Non-transient error: throw immediately.
            response.EnsureSuccessStatusCode();
        }

        throw new HttpRequestException(
            $"MusicBrainz request failed after {RetryDelays.Length + 1} attempts: {url}",
            lastEx);
    }
}
