# Jellyfin Concert Radar — Specification

> Server plugin for Jellyfin 10.11.x that aggregates upcoming concerts for artists in the music library from multiple sources, normalizes them, and displays them in an admin and user-facing page.

---

## 1. Goals

- Periodically enumerate music-library artists and fetch upcoming concerts for each from multiple external sources.
- Normalize all sources into a single schema and deduplicate.
- Respect source rate limits, API quotas, and ToS constraints.
- Expose:
  - An **admin** configuration page (API keys, filters, source toggles, run-now).
  - A **user-accessible** concert list page (no admin gate).
- Be resilient: if any source breaks, the rest keep working; operator can see per-source health.

## 2. Non-goals

- Ticket purchasing. Plugin only links out to the source.
- Push notifications / calendar export. Possible future work.
- Running on Jellyfin < 10.11 or > 10.11.x. Rebuild required for 10.12 (net9).
- Per-user configuration. All configuration is server-global.

## 3. Constraints

- **Runtime**: .NET 8, `Jellyfin.Controller` 10.11.6, `Jellyfin.Model` 10.11.6, `ExcludeAssets=runtime` on both.
- **License**: MIT (sources confirm permissive licenses are acceptable per the plugin template policy).
- **Persistence**: private SQLite database under `IApplicationPaths.DataPath/concertradar/concerts.db` via `Microsoft.Data.Sqlite`. Never write to the Jellyfin system database.
- **Configuration**: `BasePluginConfiguration`-derived type, server-global only. Jellyfin has no native per-user plugin config.
- **Outbound HTTP**: `IHttpClientFactory` (named client `NamedClient.Default`) + `System.Threading.RateLimiting`. No third-party HTTP wrappers.
- **No headless browser**. Plugin must run inside the Jellyfin process; bundling Chromium is not acceptable. All scrapers must work with plain HttpClient + HTML/JSON parsing.
- **No third-party Jellyfin plugin dependencies** (no `jellyfin-plugin-pages`, no home-sections).
- **ABI lock**: target `Jellyfin.Controller` 10.11.6 exactly; manifest pins ABI.

## 4. Sources

All six requested sources are included. Three via official APIs, three via scraping. Each is an `ISourceAdapter`.

| Source | Method | Artist resolution | Native filters | Auth | Notes |
|---|---|---|---|---|---|
| Ticketmaster | Discovery API v2 | name → `attractionId` (cached) | `countryCode`, `classificationId`, `city`, `postalCode`, `latlong`+`radius`, `marketId` | API key (free tier: 5000/day, 5 req/s — verify via `Rate-Limit-*` response headers) | Live quota surfaced in admin UI |
| Bandsintown | Public `app_id` API | MBID-native (`mbid_<uuid>`) | `location=city,country` or `lat,lon` + `radius` (max 150 mi) | `app_id` query param (self-assigned) | No API key process; gray-area ToS → personal-use framing in README |
| EdmTrain | Public API | `/artists?name=` → internal ID (cached) | `locationIds[]`, `state`, `latitude`+`longitude` | API key via manual approval; attribution required in UI | EDM-only; opt-in |
| Songkick | Scrape (HTML + MusicBrainz URL-rel) | MusicBrainz `/ws/2/artist/{mbid}?inc=url-rels` → Songkick URL; fallback to `songkick.com/search?query=` | Client-side (no per-request location filter on scrape path) | None | SSR HTML, stable DOM; 1 req/s |
| Dice.fm | Scrape (`__NEXT_DATA__` extraction) | Local slug cache seeded from `dice.fm/search?q=` | Client-side | None | Cloudflare 403 without realistic UA; circuit-breaker |
| Resident Advisor | Scrape (reverse-engineered GraphQL) | `eventListings` query; artist slug cache | `areas`, date range | None | ToS-risky; default OFF; explicit opt-in + warning banner |

### Source status & circuit breaker

Each adapter reports a status: `Ok | Degraded | Failing | Disabled | Unconfigured`.
- After N consecutive failures (configurable, default 5), adapter auto-transitions to `Failing` and is skipped for M hours (default 24). Admin UI shows status per source.
- Admin can manually re-enable a `Failing` source.

## 5. Data model

### SQLite schema

```sql
CREATE TABLE concerts (
  id               TEXT PRIMARY KEY,           -- local uuid
  source           TEXT NOT NULL,              -- ticketmaster|bandsintown|edmtrain|songkick|dice|ra
  source_event_id  TEXT NOT NULL,
  source_url       TEXT NOT NULL,              -- direct link back to the source page
  artist_mbid      TEXT,                       -- nullable
  artist_name      TEXT NOT NULL,
  event_datetime   TEXT NOT NULL,              -- ISO 8601 with offset
  venue_name       TEXT,
  venue_address    TEXT,
  city             TEXT,
  region           TEXT,
  country          TEXT,
  lat              REAL,
  lon              REAL,
  lineup           TEXT,                       -- JSON array of names
  ticket_url       TEXT,                       -- buy link (may == source_url)
  price_min        REAL,
  price_max        REAL,
  currency         TEXT,
  onsale_at        TEXT,
  fetched_at       TEXT NOT NULL,
  last_seen_at     TEXT NOT NULL,
  UNIQUE(source, source_event_id)
);
CREATE INDEX ix_concerts_datetime       ON concerts(event_datetime);
CREATE INDEX ix_concerts_artist_date    ON concerts(artist_mbid, event_datetime);
CREATE INDEX ix_concerts_geo_date       ON concerts(country, city, event_datetime);

CREATE TABLE artists (
  id                 TEXT PRIMARY KEY,         -- mbid when present; else sha1(name)
  mbid               TEXT,
  name               TEXT NOT NULL,
  jellyfin_item_id   TEXT,                     -- Jellyfin MusicArtist Guid
  ext_ids            TEXT NOT NULL DEFAULT '{}', -- JSON {source: externalId}
  last_checked_at    TEXT,                     -- nullable; NULLS FIRST for round-robin
  last_error         TEXT,
  consecutive_errors INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX ix_artists_last_checked ON artists(last_checked_at);

CREATE TABLE source_state (
  source              TEXT PRIMARY KEY,
  status              TEXT NOT NULL,          -- Ok|Degraded|Failing|Disabled|Unconfigured
  consecutive_errors  INTEGER NOT NULL DEFAULT 0,
  disabled_until      TEXT,
  calls_today         INTEGER NOT NULL DEFAULT 0,
  calls_today_reset   TEXT,
  next_allowed_at     TEXT,                   -- rate-limiter persistence
  last_error          TEXT,
  last_success_at     TEXT
);

CREATE TABLE schema_version (
  version INTEGER PRIMARY KEY,
  applied_at TEXT NOT NULL
);
```

### `PluginConfiguration`

```csharp
public class PluginConfiguration : BasePluginConfiguration
{
    // credentials
    public string TicketmasterApiKey { get; set; } = "";
    public string BandsintownAppId { get; set; } = "";
    public string EdmTrainApiKey { get; set; } = "";

    // source toggles
    public HashSet<string> EnabledSources { get; set; } =
        new() { "ticketmaster", "bandsintown", "songkick" };
    public bool AcceptRaScrapeTos { get; set; } = false;
    public bool AcceptDiceScrapeTos { get; set; } = false;

    // filters
    public List<LocationFilter> Locations { get; set; } = new();
    public HashSet<string> CountryAllowlist { get; set; } = new(); // ISO-3166 alpha-2
    public HashSet<string> GenreAllowlist { get; set; } = new();
    public int MinDaysAhead { get; set; } = 0;
    public int MaxDaysAhead { get; set; } = 365;
    public bool SkipFestivals { get; set; } = false;
    public bool SkipSoldOut { get; set; } = false;

    // scheduler
    public int MaxArtistsPerRun { get; set; } = 50;
    public int PerSourceDailyBudget { get; set; } = 500;
    public int StaleRecordDays { get; set; } = 14;
    public int CircuitBreakerThreshold { get; set; } = 5;
    public int CircuitBreakerCooldownHours { get; set; } = 24;

    // rate limits (per source overrides)
    public Dictionary<string, RateLimitConfig> RateLimits { get; set; } = new();
}

public class LocationFilter
{
    public string City { get; set; } = "";
    public string Region { get; set; } = "";
    public string Country { get; set; } = ""; // ISO-3166 alpha-2
    public double? Lat { get; set; }
    public double? Lon { get; set; }
    public int RadiusKm { get; set; } = 80;
}

public class RateLimitConfig
{
    public double RequestsPerSecond { get; set; }
    public int? RequestsPerDay { get; set; }
}
```

Default rate limits (overridable in config):

| Source | req/s | req/day |
|---|---|---|
| ticketmaster | 5 | 5000 |
| bandsintown | 1 | — |
| edmtrain | 1 | — |
| songkick | 1 | — |
| dice | 0.5 | — |
| ra | 0.5 | — |

## 6. Architecture

### Phase 1 — Ingest (scheduled task: `RefreshConcertsTask`, daily trigger)

```
ILibraryManager.GetItemList(MusicArtist, Recursive)
      │
      ▼
ArtistRepository.UpsertFromLibrary  (add new; do not delete — soft-missing)
      │
      ▼
ArtistScheduler
  • SELECT * FROM artists ORDER BY last_checked_at ASC NULLS FIRST
    LIMIT config.MaxArtistsPerRun
      │
      ▼
for each artist:
  MusicBrainzResolver.EnsureExternalIds(artist)   // one-time per artist; cached in ext_ids
      │
      ▼
  for each enabled source (with budget remaining and circuit closed):
      ISourceAdapter.FetchAsync(artistRef, sourceFilter, ct)  // rate-limited per host
        → IAsyncEnumerable<RawEvent>
      │
      ▼
      Normalizer.Normalize(rawEvent, source, artist) → ConcertRecord
      │
      ▼
      ConcertRepository.Upsert(record)            // by (source, source_event_id)
  update artist.last_checked_at / last_error
      │
      ▼
GC:
  delete concerts where event_datetime < now()                 (past-event purge)
  delete concerts where last_seen_at < now() - StaleRecordDays (stale purge)
```

### Phase 2 — Surface

- **Admin config page**: `IHasWebPages → PluginPageInfo{ Name="concertradar", EmbeddedResourcePath="...Web.admin.html" }`.
- **User concerts view**: `PluginPageInfo{ Name="concertradar-view", EmbeddedResourcePath="...Web.view.html" }`. Accessed via `https://jellyfin/web/ConfigurationPage?name=concertradar-view` — this endpoint has no `[Authorize]` attribute in Jellyfin 10.11.6, so any visitor with the URL receives the page. Page JS reads the `AccessToken` from `localStorage['jellyfin_credentials']` to call plugin APIs.
- **API controller** under `/Plugins/ConcertRadar/`:
  - `[Authorize]`: user-facing reads.
  - `[Authorize(Policy=Policies.RequiresElevation)]`: admin writes (config, run-now, source toggles).

### HTTP endpoints

```
GET  /Plugins/ConcertRadar/api/concerts                [Authorize]
     ?from=<ISO>&to=<ISO>&country=<CC>&city=<str>&source=<id>
     &artistMbid=<uuid>&page=<n>&pageSize=<n>&sort=<date|artist>
     → { items: ConcertDto[], total: int }

GET  /Plugins/ConcertRadar/api/artists                 [Authorize]
     → { items: ArtistDto[] }          // name, mbid, lastCheckedAt, resolvedSources

GET  /Plugins/ConcertRadar/api/status                  [Authorize]
     → { sources: SourceStatusDto[], lastRun, nextRun, queueSize }

POST /Plugins/ConcertRadar/api/admin/run-now           [RequiresElevation]
     → 202 Accepted; kicks RefreshConcertsTask
POST /Plugins/ConcertRadar/api/admin/sources/{id}/reset [RequiresElevation]
     → clears circuit breaker
POST /Plugins/ConcertRadar/api/admin/purge              [RequiresElevation]
     → deletes all concerts (forces re-fetch)
```

## 7. Adapter contract

```csharp
public enum SourceKind { Api, Scrape }

public interface ISourceAdapter
{
    string Id { get; }                            // "ticketmaster"
    string DisplayName { get; }
    SourceKind Kind { get; }
    bool RequiresCredentials { get; }
    bool RequiresTosOptIn { get; }

    bool IsConfigured(PluginConfiguration cfg);
    IAsyncEnumerable<RawEvent> FetchAsync(
        ArtistRef artist,
        SourceFilter filter,
        CancellationToken ct);
}

public record ArtistRef(
    string Name,
    string? Mbid,
    IReadOnlyDictionary<string, string> ExternalIds);

public record SourceFilter(
    IReadOnlyList<LocationFilter> Locations,
    IReadOnlySet<string> CountryAllowlist,
    IReadOnlySet<string> GenreAllowlist,
    DateTimeOffset MinDate,
    DateTimeOffset MaxDate,
    bool SkipFestivals,
    bool SkipSoldOut);

public record RawEvent(
    string SourceEventId,
    string SourceUrl,
    string ArtistName,
    DateTimeOffset EventDateTime,
    string? VenueName,
    string? VenueAddress,
    string? City,
    string? Region,
    string? Country,
    double? Lat,
    double? Lon,
    IReadOnlyList<string> Lineup,
    string? TicketUrl,
    decimal? PriceMin,
    decimal? PriceMax,
    string? Currency,
    DateTimeOffset? OnSaleAt,
    bool IsFestival,
    bool IsSoldOut);
```

## 8. Rate limiting

`HostRateLimiter` singleton, keyed by source id.
- Per-source `System.Threading.RateLimiting.TokenBucketRateLimiter` (tokens = `RequestsPerSecond`).
- Daily-budget counter in `source_state.calls_today`, reset at midnight UTC.
- Respect `Retry-After` header; if present, set `next_allowed_at` accordingly.
- Adapter calls `await rateLimiter.AcquireAsync(sourceId, ct)` before every HTTP call.

## 9. Round-robin scheduler

- Fresh artists (`last_checked_at IS NULL`) jump to the front (NULLS FIRST).
- Freshly-checked artists land at the back.
- With 500 artists and `MaxArtistsPerRun=50`, full library rotates every 10 days. Tunable.
- On all-source failure for an artist, `last_checked_at` is **not** updated (retried next run). On partial failure, it is updated but `last_error` is recorded.
- `PerSourceDailyBudget` stops calling a source once exhausted; remaining artists just skip that source this run.

## 10. Failure & resilience

- Per-source circuit breaker (threshold + cooldown).
- Per-adapter retry: 3 attempts with exponential backoff (200ms, 800ms, 3.2s) on transient errors (5xx, 429, network).
- Scraper fixture tests in CI ensure DOM/JSON shape hasn't changed (records are checked into the test project under `tests/fixtures/`).
- Admin UI shows `SourceStatusDto` per source: `status`, `lastSuccessAt`, `lastError`, `callsToday`, `disabledUntil`.

## 11. MusicBrainz resolver

Responsibilities:
1. If artist has no MBID, try to resolve via MusicBrainz search (`/ws/2/artist?query=artist:"<name>"`).
2. Given an MBID, fetch `/ws/2/artist/{mbid}?inc=url-rels` and extract known URL relationships:
   - Songkick → `ext_ids["songkick"]`
   - Bandsintown → `ext_ids["bandsintown"]`
   - Resident Advisor → `ext_ids["ra"]`
3. Rate-limited to 1 req/s per MusicBrainz ToS. User-Agent: `JellyfinConcertRadar/<version> (+<repo-url>)`.
4. Results cached in `artists.ext_ids`. Re-resolved only when `ext_ids == "{}"` or admin issues a `/resolve-ids` command.

## 12. Admin UI (embedded `admin.html`)

Sections:
- **Status** — last run, next run, queue length, per-source status cards (color coded), buttons: "Run now", "Reset source", "Purge all".
- **Credentials** — API keys for TM, Bandsintown, EdmTrain.
- **Sources** — toggles per source; opt-in checkboxes for Dice (ToS) and RA (ToS) with explanatory text.
- **Filters** — location list editor (add city/country/coords/radius), country allowlist multi-select, genre allowlist, min/max days ahead, skip-festivals, skip-soldout.
- **Scheduler** — `MaxArtistsPerRun`, `PerSourceDailyBudget`, `StaleRecordDays`, circuit breaker knobs.
- **Rate limits** — table: source, req/s, req/day, override inputs.

Uses `ApiClient.getPluginConfiguration(pluginId)` / `ApiClient.updatePluginConfiguration` (Jellyfin globals present when page is loaded in Dashboard).

## 13. User UI (embedded `view.html`)

Sections:
- Filter bar: date range, country, city, source, artist.
- Sort: by date (default), by artist, by city.
- List: card per concert with:
  - Date/time (user TZ).
  - Artist name (link to Jellyfin artist page if mbid matches).
  - Venue + address + city, country.
  - Lineup (if more than headliner).
  - Price range (if known).
  - "View on {Source}" button → `source_url`.
  - "Buy tickets" button → `ticket_url` (if distinct).
  - Source pill.
- Pagination: 50 per page.
- Read-only. No config controls here.

Authentication flow when visited directly via `/web/ConfigurationPage?name=concertradar-view`:
1. Read `localStorage['jellyfin_credentials']` JSON.
2. Extract `Servers[0].AccessToken`, `Servers[0].UserId`, `Servers[0].ManualAddress` (base URL).
3. Use as `X-Emby-Authorization` / `Authorization` headers for plugin API calls.
4. If missing → redirect to `/web/index.html#/login.html`.

## 14. Packaging & distribution

- `build.yaml` for JPRM.
- `manifest.json` with `category`, `guid`, versions array (`version`, `changelog`, `targetAbi=10.11.0.0`, `sourceUrl`, `checksum`, `timestamp`).
- GitHub Actions workflow: on tag `v*`, run `dotnet publish`, run JPRM, upload artifact, update `manifest.json` on `gh-pages`.
- Users add the repository URL (`https://<owner>.github.io/jellyfin-concert-radar/manifest.json`) in Jellyfin Dashboard → Plugins → Repositories.

## 15. Open risks

- Ticketmaster live quota (verify `Rate-Limit-*` headers on first call; plugin surfaces remaining quota in admin UI).
- Scraper drift for Dice/RA/Songkick — mitigated by fixture-based contract tests + circuit breaker.
- Cloudflare escalation on Dice — plugin sets realistic `User-Agent` and `Accept-Language`; on persistent 403 the circuit disables the adapter and surfaces a message.
- Absent MBIDs in library → name-based fallback lookups with lower confidence; matches with confidence below threshold are stored but flagged.
- Jellyfin 10.12 will require a rebuild against net9.
