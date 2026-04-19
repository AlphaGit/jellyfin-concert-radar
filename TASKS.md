# Jellyfin Concert Radar — Implementation Tasks

Tags:
- `[BE]` backend / .NET plugin code
- `[FE]` frontend / embedded HTML + JS
- `[INFRA]` repo, CI, packaging
- `[TEST]` test code (mirrors tests in `TESTS.md`)
- `[DOC]` docs / READMEs

Dependencies are called out explicitly. Tasks in the same phase without dependencies can run in parallel. Each task is independently reviewable.

---

## Phase 0 — Bootstrap

### T0.1 [INFRA] Initialize repository
- `git init` in `jellyfin-concert-radar/`.
- Add `.gitignore` (bin, obj, .vs, *.user, .idea, test artifacts).
- Add `LICENSE` file (MIT, current year, author name).
- Add top-level `README.md` stub (title + one-paragraph description + link to SPEC.md).
- First commit: "chore: scaffold repo".
- **Depends on**: none.
- **Deliverable**: repo with SPEC.md, TASKS.md, TESTS.md, LICENSE, README.md, .gitignore.

### T0.2 [INFRA] .NET project scaffold
- Create `src/Jellyfin.Plugin.ConcertRadar/Jellyfin.Plugin.ConcertRadar.csproj` targeting `net9.0` (Jellyfin 10.11.x is net9).
- Reference `Jellyfin.Controller` 10.11.6, `Jellyfin.Model` 10.11.6 with `<ExcludeAssets>runtime</ExcludeAssets>`.
- Reference `Microsoft.Data.Sqlite` 9.x, `AngleSharp` (canonical HTML parser for all scrapers — do not add HtmlAgilityPack).
- Create `Plugin.cs` inheriting `BasePlugin<PluginConfiguration>`, implementing `IHasWebPages` (returns empty for now).
- Create `Configuration/PluginConfiguration.cs` with stub fields.
- Verify `dotnet build` succeeds.
- **Depends on**: T0.1.

### T0.3 [INFRA] Test project scaffold
- Create `tests/Jellyfin.Plugin.ConcertRadar.Tests/Jellyfin.Plugin.ConcertRadar.Tests.csproj`.
- Reference xUnit, FluentAssertions, NSubstitute, Microsoft.Data.Sqlite (for in-memory test repos).
- One sanity test: `Plugin_Guid_IsStable()`.
- Verify `dotnet test` passes.
- **Depends on**: T0.2.

### T0.4 [INFRA] CI workflow
- Add `.github/workflows/build.yml`: setup .NET 8, `dotnet restore`, `dotnet build --configuration Release`, `dotnet test`.
- Matrix: ubuntu-latest only for now.
- **Depends on**: T0.3.

### T0.5 [INFRA] JPRM packaging baseline
- Add `build.yaml` with plugin metadata (name, guid, description, category, targetAbi=10.11.0.0, framework=net8.0, artifacts).
- Add placeholder `manifest.json`.
- GH Actions job `package.yml` triggered on tag `v*`: run `jprm` CLI, upload artifact.
- **Depends on**: T0.4.

---

## Phase 1 — Core storage & configuration

### T1.1 [BE] `PluginConfiguration` full shape
- Implement all fields per SPEC §5.
- Default values match spec table.
- XML-serializable (Jellyfin persists config as XML).
- **Depends on**: T0.2.

### T1.2 [BE] `IApplicationPaths`-based DB path resolver
- `DatabaseLocator` class: `Path.Combine(_paths.DataPath, "concertradar", "concerts.db")`. Creates directory if missing.
- Injectable via constructor; singleton in DI.
- **Depends on**: T0.2.

### T1.3 [BE] Schema migration runner
- `SchemaMigrator` class with ordered migrations (`001_initial.sql`, etc.).
- `schema_version` table tracks applied versions.
- Idempotent; safe to run on every plugin startup.
- First migration applies the full SPEC §5 schema.
- **Depends on**: T1.2.

### T1.4 [BE] `ConcertRepository`
- CRUD for concerts table. Methods:
  - `UpsertAsync(ConcertRecord r, CancellationToken ct)`
  - `QueryAsync(ConcertQuery q, CancellationToken ct) → QueryResult<ConcertRecord>`
  - `MarkSeenAsync(string source, string sourceEventId, DateTimeOffset now)`
  - `DeletePastAsync(DateTimeOffset now)`
  - `DeleteStaleAsync(DateTimeOffset threshold)`
  - `PurgeAllAsync()`
- Uses `Microsoft.Data.Sqlite`. Uses parameterized queries. No string concatenation.
- **Depends on**: T1.3.

### T1.5 [BE] `ArtistRepository`
- Methods:
  - `UpsertFromLibraryAsync(IEnumerable<LibraryArtist> items)` (adds new, updates `jellyfin_item_id`/`name`/`mbid`; does not delete)
  - `GetNextBatchAsync(int limit)` → ordered by `last_checked_at` ASC NULLS FIRST
  - `UpdateCheckedAsync(string id, DateTimeOffset now, string? error)`
  - `SetExternalIdsAsync(string id, IReadOnlyDictionary<string,string> ids)`
  - `ResetAllCheckedAsync()`
- **Depends on**: T1.3.

### T1.6 [BE] `SourceStateRepository`
- Methods:
  - `GetAllAsync()`
  - `RecordSuccessAsync(string source, DateTimeOffset now)`
  - `RecordFailureAsync(string source, string error, DateTimeOffset now)` (bumps `consecutive_errors`, may open circuit)
  - `IsOpenAsync(string source, DateTimeOffset now)` → true if `disabled_until > now`
  - `IncrementCallCounterAsync(string source)` with midnight-UTC reset
  - `GetAllowanceAsync(string source)` → remaining daily budget
  - `ResetAsync(string source)` (clears circuit)
- **Depends on**: T1.3.

### T1.7 [BE] DI registration (`IPluginServiceRegistrator`)
- Register repositories as singletons.
- Register adapters (empty for now) as `IEnumerable<ISourceAdapter>`.
- **Depends on**: T1.4, T1.5, T1.6.

---

## Phase 2 — Rate limiting, HTTP, library access, resolver

### T2.1 [BE] `HostRateLimiter`
- Keyed by source id. Backed by `TokenBucketRateLimiter` from `System.Threading.RateLimiting`.
- `AcquireAsync(sourceId, ct)` waits until a token is available.
- Also checks `SourceStateRepository.GetAllowanceAsync` for daily budget.
- Respects `Retry-After` — `SetBackoffAsync(sourceId, until)` sets a temporary floor.
- **Depends on**: T1.6.

### T2.2 [BE] `LibraryArtistEnumerator`
- Wraps `ILibraryManager.GetItemList`. Yields `LibraryArtist { JellyfinId, Name, Mbid }`.
- Reads `artist.ProviderIds["MusicBrainzArtist"]`; splits comma-separated MBIDs, takes first.
- **Depends on**: T0.2.

### T2.3 [BE] `MusicBrainzResolver`
- Client for `musicbrainz.org/ws/2` with 1 req/s rate limit (separate `HostRateLimiter` instance).
- `Task<string?> ResolveMbidByNameAsync(string name, ct)` — `/artist?query=artist:"<name>"` returns best match with score ≥ threshold.
- `Task<IReadOnlyDictionary<string,string>> FetchUrlRelsAsync(string mbid, ct)` — returns `{source: externalId}` for known relationship types:
  - `songkick` → from `songkick.com/artists/<id>-<slug>` URL
  - `bandsintown` → from URL; also just-use-MBID since BIT accepts `mbid_<uuid>`
  - `ra` → from `ra.co/dj/<slug>` URL
- Cached via `ArtistRepository.SetExternalIdsAsync`.
- User-Agent set per SPEC §11.
- **Depends on**: T2.1, T1.5.

### T2.4 [BE] `Normalizer`
- Pure functions: `Normalize(RawEvent e, string source, ArtistRef artist) → ConcertRecord`.
- Generates `id` as new uuid. Validates mandatory fields (source_url, event_datetime, artist_name).
- Parses varied datetime formats into `DateTimeOffset`.
- Applies post-fetch filters (country allowlist, min/max days ahead, skip-festivals, skip-soldout) if the source didn't filter natively.
- **Depends on**: T1.1.

---

## Phase 3 — First adapter & scheduled task (Ticketmaster)

### T3.1 [BE] `TicketmasterAdapter`
- Calls Discovery API v2 `/events.json`.
- Resolves artist → attraction id (GET `/attractions.json?keyword=<name>`) once; caches via `ArtistRepository.SetExternalIdsAsync`.
- Query params: `attractionId`, `countryCode`, `classificationId` (mapped from genre allowlist), `startDateTime`, `endDateTime`, `size=200`.
- Paginates via `page[number]`.
- Parses response → `RawEvent` stream.
- Logs `Rate-Limit-*` headers; reports remaining quota to `SourceStateRepository`.
- Transient-error retry (SPEC §10).
- **Depends on**: T2.1, T2.4.

### T3.2 [BE] `RefreshConcertsTask` (`IScheduledTask`)
- Default trigger: daily at 03:00 local time.
- Steps per SPEC §6 "Phase 1 — Ingest".
- Reports progress 0–100.
- Cancellation-aware (honors `CancellationToken`).
- Writes full run stats to logs: artists processed, per-source call counts, errors.
- **Depends on**: T1.4, T1.5, T2.1, T2.2, T2.3, T3.1.

### T3.3 [BE] `ConcertsController` (read endpoints)
- `[ApiController][Route("Plugins/ConcertRadar/api")]`.
- Implements `GET /concerts`, `GET /artists`, `GET /status` per SPEC §6.
- `[Authorize]` on all.
- Paginates, sorts, filters via `ConcertQuery`.
- DTOs: `ConcertDto`, `ArtistDto`, `SourceStatusDto`.
- **Depends on**: T1.4, T1.5, T1.6.

### T3.4 [BE] `AdminController` (write endpoints)
- `[Authorize(Policy=Policies.RequiresElevation)]`.
- `POST /admin/run-now` → schedules `RefreshConcertsTask` via `ITaskManager`.
- `POST /admin/sources/{id}/reset` → `SourceStateRepository.ResetAsync`.
- `POST /admin/purge` → `ConcertRepository.PurgeAllAsync`.
- `POST /admin/resolve-ids` → clears `ext_ids` for all artists (forces re-resolution).
- **Depends on**: T1.4, T1.6, T3.2.

---

## Phase 4 — Admin UI (minimum viable)

### T4.1 [FE] `Web/admin.html` scaffold
- Single-file HTML with embedded `<style>` and `<script>` blocks.
- Uses Jellyfin's global `ApiClient` (present when loaded inside Dashboard).
- Page structure per SPEC §12 — sections stubbed, no functionality yet.
- Registered via `IHasWebPages.GetPages()`.
- **Depends on**: T0.2.

### T4.2 [FE] Config load/save
- On page open: `ApiClient.getPluginConfiguration(pluginId)` → populate form.
- On save: `ApiClient.updatePluginConfiguration(pluginId, config)`.
- Error surface: toast / inline validation.
- **Depends on**: T4.1, T1.1.

### T4.3 [FE] Status section
- Calls `/Plugins/ConcertRadar/api/status` every 5s while visible.
- Renders per-source cards: color-coded badge, `lastSuccessAt`, `callsToday`, `disabledUntil`.
- Buttons: "Run now", "Reset source", "Purge all".
- **Depends on**: T4.1, T3.3, T3.4.

### T4.4 [FE] Filters editor
- Location list: add/remove rows (city, region, country, lat, lon, radius).
- Country allowlist: multi-select (ISO-3166 dropdown).
- Genre allowlist: tag input.
- Min/max days, skip-festivals, skip-soldout checkboxes.
- **Depends on**: T4.2.

### T4.5 [FE] Sources & credentials
- Toggles per source (checkbox).
- API key inputs for TM/Bandsintown/EdmTrain (password-style).
- ToS opt-in checkboxes for Dice and RA with explanatory warning text.
- **Depends on**: T4.2.

### T4.6 [FE] Scheduler + rate limits
- Numeric inputs: `MaxArtistsPerRun`, `PerSourceDailyBudget`, `StaleRecordDays`, circuit-breaker knobs.
- Per-source rate limit table: req/s, req/day with defaults and overrides.
- **Depends on**: T4.2.

---

## Phase 5 — Second adapter (Bandsintown)

### T5.1 [BE] `BandsintownAdapter`
- `GET https://rest.bandsintown.com/artists/{artistIdentifier}/events?app_id=<id>`.
- `artistIdentifier` = `mbid_<uuid>` when MBID present; else URL-encoded name.
- Parses array response → `RawEvent` stream.
- Location filtering is client-side (BIT's filter works but requires separate query per location).
- **Depends on**: T2.1, T2.4.

### T5.2 [BE] Register BandsintownAdapter in DI
- Added to `IEnumerable<ISourceAdapter>`.
- Scheduled task picks it up automatically when enabled in config.
- **Depends on**: T5.1, T1.7.

---

## Phase 6 — User concerts view

### T6.1 [FE] `Web/view.html` scaffold
- Single-file HTML. Detects load context: inside Jellyfin web shell vs. direct URL visit.
- If direct visit and no `AccessToken` in localStorage → redirect to `/web/index.html#/login.html`.
- **Depends on**: T0.2.

### T6.2 [FE] View: auth + API wiring
- Reads `jellyfin_credentials` from localStorage; extracts `AccessToken`, `UserId`, `ManualAddress`.
- Builds `X-Emby-Authorization` header per Jellyfin spec.
- Small `api.js`-style module for fetch wrappers.
- **Depends on**: T6.1, T3.3.

### T6.3 [FE] View: filter bar
- Date range picker (default: now → +180 days).
- Country / city / source dropdowns populated from the server (`/api/status` + `/api/concerts?facets=true` OR client-side distinct).
- Artist autocomplete against `/api/artists`.
- **Depends on**: T6.2.

### T6.4 [FE] View: results list
- Cards rendered per SPEC §13.
- Pagination controls.
- Sort dropdown (date, artist, city).
- Empty-state + error-state messages.
- **Depends on**: T6.2.

### T6.5 [FE] View: register page & route
- `PluginPageInfo{ Name="concertradar-view", EmbeddedResourcePath="...Web.view.html" }`.
- README snippet for users: "Bookmark this URL".
- **Depends on**: T6.1.

---

## Phase 7 — EdmTrain adapter

### T7.1 [BE] `EdmTrainAdapter`
- `GET https://edmtrain.com/api/events?client=<key>&locationIds=...&startDate=...` etc.
- Name → `artistId` resolution via `/artists?name=...`; cached.
- Parses response → `RawEvent` stream.
- **Depends on**: T2.1, T2.4.

### T7.2 [BE] Register + UI attribution
- Register adapter in DI.
- Admin UI shows required attribution text when EdmTrain is enabled.
- User view shows "Data: EdmTrain" badge on EdmTrain rows.
- **Depends on**: T7.1, T1.7, T4.5.

### T7.3 [FE] Attribution badges (EdmTrain ToS follow-up)
- Show "Data: EdmTrain" badge in admin status card when the EdmTrain source is enabled.
- Show "Data: EdmTrain" badge on each EdmTrain row in the user concert view (`view.html`).
- Non-blocking for v0 ship, but required before ToS compliance.
- **Depends on**: T7.2, T4.3, T6.4.

---

## Phase 8 — Songkick scraper

### T8.1 [BE] `SongkickScrapeAdapter`
- Resolves artist URL:
  - Preferred: `ext_ids["songkick"]` (populated by `MusicBrainzResolver`).
  - Fallback: `GET https://www.songkick.com/search?query=<name>&type=artists`, parse first result.
- Fetches `https://www.songkick.com/artists/<id>-<slug>`.
- Parses upcoming-events block via AngleSharp/HtmlAgilityPack (stable selectors: `.event-listing`, `.microformat`).
- Extracts: date (from `time[datetime]`), venue, city, country, event URL.
- Realistic `User-Agent`.
- **Depends on**: T2.1, T2.3, T2.4.

### T8.2 [BE] Register + health heuristics
- Register in DI.
- Adapter reports `Degraded` if selector misses on >20% of pages in a run.
- **Depends on**: T8.1, T1.7.

---

## Phase 9 — Dice.fm scraper

### T9.1 [BE] `DiceScrapeAdapter`
- Resolves slug:
  - Cache-first via `ext_ids["dice"]`.
  - `GET https://dice.fm/search?q=<name>`, parse `__NEXT_DATA__` for artist results.
  - Stores `<slug>-<suffix>` in ext_ids.
- Fetches `https://dice.fm/artist/<slug>` with realistic UA + `Accept-Language: en-US`.
- Extracts `<script id="__NEXT_DATA__">` content, deserializes JSON.
- Walks JSON to extract events.
- On repeated 403 responses: triggers circuit breaker, status surfaces in admin UI.
- **Depends on**: T2.1, T2.4.

### T9.2 [BE] Register + opt-in gate
- Register in DI.
- Adapter skipped if `!config.AcceptDiceScrapeTos`.
- **Depends on**: T9.1, T1.7.

---

## Phase 10 — Resident Advisor scraper

### T10.1 [BE] `RaScrapeAdapter`
- `POST https://ra.co/graphql` with `eventListings(filters:{areas:..., listingDateGte:..., listingDateLte:...}, ...)`.
- Artist resolution: cache `ext_ids["ra"]` from MusicBrainz URL-rel; fallback to RA's `artist(slug:...)` query with name-to-slug best-guess.
- Parses response → `RawEvent` stream.
- Default OFF; requires `config.AcceptRaScrapeTos`.
- **Depends on**: T2.1, T2.4.

### T10.2 [BE] Register + prominent warning
- Register in DI.
- Admin UI shows red warning banner when RA is enabled.
- **Depends on**: T10.1, T1.7, T4.5.

---

## Phase 11 — Hardening, GC, circuit breaker

### T11.1 [BE] GC pass in `RefreshConcertsTask`
- After successful run, run `DeletePastAsync` and `DeleteStaleAsync`.
- Logs counts.
- **Depends on**: T3.2.

### T11.2 [BE] Wire circuit breaker into adapters
- All adapters check `SourceStateRepository.IsOpenAsync` before making calls.
- On transient failure, call `RecordFailureAsync`; on success, `RecordSuccessAsync`.
- Threshold + cooldown from config.
- **Depends on**: T1.6, all adapters (T3.1, T5.1, T7.1, T8.1, T9.1, T10.1).

### T11.3 [BE] Structured logging
- Use `ILogger<T>` throughout.
- Log levels: Information (run start/end, adapter success counts), Warning (retries, transient failures), Error (circuit breaker trips, unexpected exceptions).
- **Depends on**: all BE tasks.

---

## Phase 12 — Packaging, docs, release

### T12.1 [INFRA] Finalize JPRM packaging
- Fill `build.yaml` with correct `targetAbi`, `framework`, `guid`, dependencies.
- `manifest.json` with versions array.
- Smoke test: install built artifact on a local Jellyfin 10.11.6.
- **Depends on**: all previous tasks except T12.2, T12.3.

### T12.2 [INFRA] `manifest.json` hosting via GH Pages
- `gh-pages` branch with `manifest.json` + release artifacts.
- GH Actions workflow appends a new version on every tag push.
- **Depends on**: T12.1.

### T12.3 [DOC] README
- Install steps (manual + repository URL).
- Configuration walkthrough with screenshots.
- Source status reference (which sources, which require API keys, ToS notes).
- Known limitations.
- **Depends on**: T4.*, T6.*.

### T12.4 [DOC] CHANGELOG
- `CHANGELOG.md` with `Keep a Changelog` format.
- First entry `0.1.0 — Initial release`.
- **Depends on**: T12.3.

---

## Agent dispatch summary

- **BE agent(s)** own: T0.2, T0.3, T1.*, T2.*, T3.1–T3.4, T5.*, T7.1, T8.1, T9.1, T10.1, T11.*.
- **FE agent(s)** own: T4.*, T6.*.
- **INFRA agent(s)** own: T0.1, T0.4, T0.5, T7.2 (cross-cutting credit to FE for UI bits), T12.1, T12.2.
- **DOC agent(s)** own: T12.3, T12.4.
- **TEST tasks** are embedded in each feature; see `TESTS.md` for the full list.

Recommended build order for the minimum useful system: Phases 0 → 1 → 2 → 3 → 4 (admin works end-to-end with Ticketmaster only). Phases 5, 6, 7, 8, 9, 10 can overlap between agents once Phase 4 lands. Phase 11 and 12 are the hardening/release gate.

---

## Phase 13 — Post-review fixes

Findings from the full code review performed after Phase 12 landed. Each task owns exactly one finding. Severity and owning agent tagged; implement in severity order.

### Previously flagged (known before review)

#### T13.0a [BE] Inject `IPluginConfigurationProvider` into adapters
- All six source adapters currently read `Plugin.Instance?.Configuration` directly. Forces `PluginInstanceCollection` serial test collection.
- Fix: constructor-inject `IPluginConfigurationProvider` (seam already exists for `HostRateLimiter`); remove the `PluginInstanceCollection` attribute from the adapter test files it unblocks.

#### T13.0b [FE] EdmTrain attribution badges
- Show "Data: EdmTrain" badge on the admin status card and on each EdmTrain row of the user view. Required by EdmTrain API ToS. Non-blocking for local dev, required before any redistribution.

#### T13.0c [INFRA] JPRM release pipeline
- Wire `gh-pages` branch hosting of `manifest.json` + release zip. On tag push, append the new version to `manifest.json` and publish.

### Review findings (severity-ordered)

#### T13.1 [BE] CRITICAL — Freshly-resolved external IDs discarded
- `ScheduledTasks/RefreshConcertsTask.cs` lines 125–156: after `SetExternalIdsAsync` persists new ext_ids, the code still reads `artist.ExternalIds` (stale snapshot from `GetNextBatchAsync`). Adapters fall back to slow name-lookup every run until the NEXT run.
- Fix: use the freshly-returned `extIds` dict when constructing `ArtistRef` if `extIds.Count > 0`.

#### T13.2 [BE] CRITICAL — Missing Normalizer post-filters
- `Normalization/Normalizer.cs`: `SourceFilter.GenreAllowlist` and `SourceFilter.Locations` (city/country/radius) are populated by `RefreshConcertsTask.BuildSourceFilter` but never checked in `Normalize`. Dead code paths in SPEC §5 filters.
- Fix: add genre and location-radius post-filter checks. Radius calc via haversine on lat/lon.

#### T13.3 [FE] HIGH — `javascript:` URL XSS in view.html
- `Web/view.html` lines 557–562: `srcUrl` / `ticketUrl` escaped for HTML but no URL-scheme allowlist. Malicious stored URL runs JS on click.
- Fix: allowlist `https?:` only before rendering each `href` attribute; fallback = empty href + disabled button.

#### T13.4 [BE] HIGH — Midnight-crossing timezone bug
- `Storage/SourceStateRepository.cs` `HasCrossedMidnight` uses `now.Date.ToUniversalTime()` — `DateTimeOffset.Date` strips offset and returns local-kind `DateTime`; `ToUniversalTime()` on a local DateTime shifts hours. Breaks daily-budget reset on non-UTC servers.
- Fix: `new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero)`.

#### T13.5 [BE] HIGH — Duplicate `RecordSuccessAsync` in adapters
- All six adapters call `RecordSuccessAsync` inside their retry helpers; `RefreshConcertsTask` calls it again. Fires N+1 times per artist, uses `DateTimeOffset.UtcNow` (bypasses injected `TimeProvider`, breaks test determinism).
- Fix: remove `RecordSuccessAsync` from adapter retry helpers; keep only in `RefreshConcertsTask`. Replace any `DateTimeOffset.UtcNow` in adapters with injected `TimeProvider`.

#### T13.6 [BE] HIGH — API key leak in exception message
- `Sources/TicketmasterAdapter.cs` line 293 (and similar in `MusicBrainzResolver`): retry exhaustion throws `HttpRequestException` with the full URL including `apikey=<key>`. Message persists in `source_state.last_error` and returns via `/api/status` to authenticated users.
- Fix: redact `apikey=...` (and other credential query params) from URL before embedding in exception / log messages.

#### T13.7 [BE] MEDIUM — Ticketmaster attraction false-positive
- `Sources/TicketmasterAdapter.cs` line 161, 207: `ResolveAttractionIdAsync` falls back to first result when no exact name match. Common names bind to wrong attraction, cached permanently.
- Fix: add minimum-confidence check (e.g. similarity ratio ≥ 0.8) OR log a warning + skip caching so next run retries.

#### T13.8 [BE] MEDIUM — Redundant MusicBrainz resolution for zero-rel artists
- `ScheduledTasks/RefreshConcertsTask.cs` lines 125–127: resolver runs every batch for artists whose MusicBrainz lookup genuinely returned zero URL relations. No sentinel means infinite re-query.
- Fix: store `{"_resolved": "true"}` (or similar sentinel key) after a successful zero-result lookup; guard subsequent runs on that key.

#### T13.9 [BE] MEDIUM — Two round-trips per state mutation
- `Storage/SourceStateRepository.cs` `RecordFailureAsync` and `IncrementCallCounterAsync`: each calls `GetAsync` then opens a second connection for `UPDATE`. 2× latency per write.
- Fix: merge into single parameterized statement using `COALESCE` and conditional expressions.

#### T13.10 [FE] MEDIUM — `innerHTML` write of numeric config values
- `Web/admin.html` line 672 (`makeLocationRow`): `loc.lat`, `loc.lon`, `loc.radiusKm` inserted into `innerHTML` without `escHtml`. Corrupted config could render raw strings.
- Fix: apply `escHtml` to all interpolated values, or switch to `createElement`/`setAttribute`.

#### T13.11 [BE] MEDIUM — Magic retry / pagination constants
- All adapters duplicate `200ms / 800ms / 3.2s` backoff and per-adapter page caps as literals.
- Fix: extract shared `AdapterDefaults` static class (or wire through `PluginConfiguration`).

#### T13.12 [FE] LOW — Status interval leak on SPA re-entry
- `Web/admin.html` lines 926–944: `pageshow` starts interval; `pagebeforehide` cleanup wired once. Re-entry spawns duplicate intervals.
- Fix: call `stopStatusPolling()` at top of `pageshow` before starting new interval.

#### T13.13 [BE] LOW — Race between DI schema bootstrap and first API request
- `SchemaBootstrapHostedService.StartAsync` migrates synchronously during startup but API controllers register before `IHostedService.StartAsync` completes. A request in the first ms can hit the repo before schema exists.
- Fix: gate first-request handling on `IHostApplicationLifetime.ApplicationStarted`, or add a `PRAGMA user_version` guard in repository `OpenAsync`.

#### T13.14 [TEST] LOW — Flaky real-time delay in rate-limiter test
- `tests/.../RateLimiting/HostRateLimiterTests.cs` line 215: uses `Task.Delay(100)` to wait for async backoff. Can race on loaded CI.
- Fix: replace with stub-clock advancement or `ManualResetEventSlim` signal.

#### T13.15 [BE] NIT — Songkick missing ToS opt-in gate
- `Sources/SongkickScrapeAdapter.cs` sets `RequiresTosOptIn => false` but Songkick's ToS prohibits scraping just as Dice/RA do. Inconsistent with DICE/RA pattern.
- Fix: add `AcceptSongkickScrapeTos` to `PluginConfiguration`; set `RequiresTosOptIn => true`; wire an opt-in checkbox in `admin.html`.

---

## Phase 14 — Security review fixes + completion gaps

Findings from second-pass code review (security-focused) + TASKS.md completion audit. Tagged per owning agent + severity.

### Security findings (severity-ordered)

#### T14.1 [FE] MEDIUM — Unescaped date strings in admin status cards (XSS)
- `Web/admin.html` `renderSourceCards`: `fmtDate(s.disabledUntil)` and `fmtDate(s.lastSuccessAt)` are concatenated into `grid.innerHTML`. `fmtDate` falls back to raw ISO string on parse failure — a crafted `disabled_until` (e.g. via malicious upstream Retry-After) lands in the admin DOM unescaped.
- Fix: wrap every `fmtDate()` call that flows into `innerHTML` with `escHtml(fmtDate(...))`.

#### T14.2 [FE] MEDIUM — Fragile class-attribute interpolation (XSS hygiene)
- `Web/admin.html` `renderSourceCards`: `badgeCls` is injected into `class="..."` attribute position. Currently safe due to dictionary lookup, but any future refactor passing `s.status` through directly would introduce XSS. Defense-in-depth.
- Fix: `escHtml(badgeCls)` at every attribute interpolation site.

#### T14.3 [BE] MEDIUM — Source-id allowlist on admin reset endpoint
- `Api/AdminController.cs` `POST /admin/sources/{id}/reset`: path parameter flows directly to `SourceStateRepository.ResetAsync`. No allowlist → admin can create arbitrary rows with unbounded string keys. Also affects `HostRateLimiter.GetOrCreateLimiter` (Finding T14.8).
- Fix: controller-level allowlist check against the six known source IDs; return `400` otherwise.

#### T14.4 [BE] MEDIUM — Unbounded HTTP response buffering (DoS)
- All six adapters + `MusicBrainzResolver` call `response.Content.ReadAsStringAsync(ct)` without setting `HttpClient.MaxResponseContentBufferSize`. A gigabyte-scale upstream response would OOM the Jellyfin process.
- Fix: set a per-client `MaxResponseContentBufferSize` (e.g. 10 MiB) via a custom `HttpClient` factory wrapper, or switch JSON paths to streaming `JsonSerializer.DeserializeAsync` on the response stream.

#### T14.5 [BE] MEDIUM — `AdminController.RunNow` reflects raw `ex.Message` to response
- `Api/AdminController.cs` line 72: `StatusCode(500, ex.Message)` sends internal error details straight to the admin browser. Downstream error strings could contain data from other plugins or internal paths that escaped `UrlRedactor`.
- Fix: return a generic `"Failed to queue the refresh task. See server logs."` body; keep `_logger.LogError(ex, …)` for operator visibility.

#### T14.6 [BE] LOW — `/api/status` exposes `LastError` to non-admin users
- `Api/ConcertsController.cs` `GET /status`: user-scoped endpoint returns `SourceStatusDto` with `LastError` populated. Even after `UrlRedactor`, error strings reveal which endpoints are probed and their failure modes.
- Fix: strip `LastError` from the DTO returned by this endpoint, OR move `/status` under the admin controller (prefer stripping — the rest of the payload is useful to users).

#### T14.7 [BE] LOW — Open redirect via scraper-supplied URL hosts
- `Sources/RaScrapeAdapter.cs` `MapToRawEvent` (around line 303) and `Sources/DiceScrapeAdapter.cs` (around line 265): `contentUrl` / `url` from scraped JSON is stored in `SourceUrl` with only a `StartsWith("http")` guard. A compromised upstream could redirect clicks to an attacker domain.
- Fix: validate that the host matches `ra.co` / `dice.fm` (or their expected subdomain) before storing. Reject/null the URL otherwise.

#### T14.8 [BE] LOW — Rate-limiter dictionary unbounded growth
- `RateLimiting/HostRateLimiter.cs` `GetOrCreateLimiter`: `_limiters.GetOrAdd(sourceId, …)` spawns a new limiter (with a replenishment timer) for every unique source id. Combined with T14.3 this enables slow memory growth from admin-driven resets.
- Fix: gate limiter creation behind the same source-id allowlist used in T14.3.

#### T14.9 [BE] LOW — `lastError` persisted without UrlRedactor on artist row
- `ScheduledTasks/RefreshConcertsTask.cs` line 242: `lastError = $"{adapter.Id}: {ex.Message}"` stored in `artists.last_error` without `UrlRedactor` protection. Most adapter throw paths already redact, but JSON parse failures mid-response can reach here with partial response bodies.
- Fix: `UrlRedactor.Redact(ex.Message)` before interpolation.

#### T14.10 [INFRA] INFO — Floating NuGet version ranges
- `src/Jellyfin.Plugin.ConcertRadar/Jellyfin.Plugin.ConcertRadar.csproj`: `Microsoft.Data.Sqlite 9.0.*` and `AngleSharp 1.*` use floating minors. Non-reproducible restores; supply-chain substitution risk if the feed is compromised.
- Fix: add `<RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>` + commit `packages.lock.json`. Alternative: pin to exact versions.

### Completion gaps (from TASKS.md audit)

#### T14.11 [FE] `CircuitBreakerCooldownHours` missing from admin UI
- `PluginConfiguration.CircuitBreakerCooldownHours` exists but has no corresponding input in `Web/admin.html` scheduler section.
- Fix: add numeric input + bind in `populateForm` / `readForm`.

#### T14.12 [INFRA] JPRM release pipeline (`gh-pages` hosting)
- `.github/workflows/package.yml` builds + uploads the zip but does NOT append to `manifest.json` on `gh-pages`.
- Fix: extend the workflow to check out `gh-pages`, append a new version entry (version, changelog, targetAbi, sourceUrl, checksum, timestamp), copy the artifact, commit, and push.

#### T14.13 [DOC] Deferred enhancements
- README "configuration walkthrough with screenshots" pending (minor).
- `docs/smoke-test.md` post-release install checklist pending.
