# Configuration Reference

All plugin settings are server-global. Jellyfin does not support per-user plugin configuration. The admin page lives at **Dashboard → Plugins → Concert Radar** and the underlying XML is persisted at:

```
/var/lib/jellyfin/plugins/configurations/<plugin-guid>.xml
```

Spec defaults for collections (`EnabledSources`, `RateLimits`) are seeded on first startup by `SchemaBootstrapHostedService`. The defaults are never re-applied after seeding, so your edits are safe across restarts.

## First-run checklist

1. Obtain credentials for the sources you want to use (see "Credentials" below).
2. Add at least one **Location** with a valid city + country + radius, or the adapters that support location filtering have nothing to target.
3. Tick the source toggles you need. Dice, Resident Advisor, and Songkick additionally require their respective ToS opt-in checkboxes.
4. Save. Hit **Run now** to fetch immediately, or wait for the daily trigger.

---

## Credentials

### `TicketmasterApiKey`
- Default: empty (source disabled).
- Obtain at [developer.ticketmaster.com](https://developer.ticketmaster.com/products-and-docs/apis/discovery-api/v2/) — free tier self-service.
- Free tier quota: 5000 requests/day, 5 requests/second. Live quota reported in response headers; the plugin surfaces it in the admin status card.

### `BandsintownAppId`
- Default: empty.
- Bandsintown's `app_id` is an arbitrary string you self-assign (e.g. `"concert-radar-<your-handle>"`). No signup required for read-only endpoints.
- Data Application Terms require caching restrictions and prohibit commercial use without approval. Review before deploying for an organization.

### `EdmTrainApiKey`
- Default: empty.
- Request at [edmtrain.com/developer-api](https://edmtrain.com/developer-api). Manual approval; usually same-day.
- ToS requires attribution in the UI. The plugin renders a "Data: EdmTrain" badge on the admin status card and each EdmTrain event row automatically.

---

## Sources

Each source has an entry in `EnabledSources`. The admin toggles flip these on/off.

| Source | Key | Opt-in gate | Notes |
|---|---|---|---|
| Ticketmaster | `ticketmaster` | — | Requires `TicketmasterApiKey`. |
| Bandsintown | `bandsintown` | — | Requires `BandsintownAppId`. MBID-native resolution. |
| EdmTrain | `edmtrain` | — | Requires `EdmTrainApiKey`. EDM-focused; narrow catalog. |
| Songkick | `songkick` | `AcceptSongkickScrapeTos` | HTML scrape. Songkick ToS prohibits scraping — opt-in is an explicit acknowledgement. |
| Dice.fm | `dice` | `AcceptDiceScrapeTos` | `__NEXT_DATA__` extraction. Cloudflare-sensitive. |
| Resident Advisor | `ra` | `AcceptRaScrapeTos` | Reverse-engineered GraphQL. Default OFF. |

Default enabled set (first run only): `ticketmaster`, `bandsintown`, `songkick`. Disable or add as needed.

### ToS flags

- `AcceptSongkickScrapeTos` (default `false`)
- `AcceptDiceScrapeTos` (default `false`)
- `AcceptRaScrapeTos` (default `false`)

A source is silently skipped at scheduler time if its `RequiresTosOptIn` is true and the matching flag is false — even if the source is in `EnabledSources`. This is defense in depth.

---

## Filters

### `Locations` (list)
Each entry defines a circle for location filtering:
- `City` — free-text city name.
- `Region` — optional state / province.
- `Country` — ISO-3166 alpha-2 (`US`, `GB`, `DE`).
- `Lat` / `Lon` — optional decimal degrees. When supplied, enables haversine-radius filtering.
- `RadiusKm` — default 80. Ignored when lat/lon absent.

Events outside every configured location are dropped post-fetch. If no `Locations` are configured, no location filter is applied (events from anywhere are kept).

### `CountryAllowlist` (list of ISO-3166 alpha-2)
Default empty = all countries. When non-empty, only events whose `Country` is in the list survive post-normalization. Case-insensitive.

### `GenreAllowlist` (list of strings)
Default empty = all genres. When non-empty, only events tagged with a matching genre pass. Adapters that lack per-event genre data (Songkick, Dice, RA) are not filtered by this rule; Ticketmaster and EdmTrain carry genre fields.

### `MinDaysAhead` / `MaxDaysAhead`
- `MinDaysAhead` default `0`. Minimum days from now before an event counts. Set to `1` or higher to skip "happening today" noise.
- `MaxDaysAhead` default `365`. Maximum lookahead window. Lower to reduce fetch volume; higher to catch on-sale announcements.

### `SkipFestivals` (bool, default `false`)
Drops events flagged as festivals. Useful for users uninterested in multi-day lineups.

### `SkipSoldOut` (bool, default `false`)
Drops events whose adapter reported sold-out status. Signal availability varies by source.

---

## Scheduler

### `MaxArtistsPerRun` (default `50`)
Number of artists processed per scheduled run, oldest-checked first (NULLs jump to the front). With 500 artists and a daily trigger, the default rotates the library every 10 days. Raise for smaller libraries or tighter freshness; lower to reduce per-run load.

### `PerSourceDailyBudget` (default `500`)
Hard cap on API calls per source per day. Counter resets at 00:00 UTC. Exhausted sources are skipped for the remainder of the run but do NOT trip the circuit breaker.

Tuning guide:
- Ticketmaster free tier: 5000/day → a budget of 500 leaves room for retries.
- Bandsintown: unlimited in practice; 500 is plenty.
- EdmTrain: unlimited documented; respect their "reasonable use" policy.
- Scrapers: lower budgets (100–200) protect against running into rate limits or bot detection.

### `StaleRecordDays` (default `14`)
Concerts not re-seen in this many days are GC'd at the end of a run. Too low → events flicker in/out as sources briefly drop them; too high → GC rarely fires.

### `CircuitBreakerThreshold` (default `5`)
Consecutive failures before a source is automatically disabled.

### `CircuitBreakerCooldownHours` (default `24`)
How long a tripped source stays disabled. Admin can manually reset via the **Reset source** button.

---

## Rate limits

`RateLimits` is a list of per-source overrides for `RequestsPerSecond` and `RequestsPerDay`. Spec defaults (seeded once):

| Source | req/s | req/day |
|---|---|---|
| ticketmaster | 5 | 5000 |
| bandsintown | 1 | — |
| edmtrain | 1 | — |
| songkick | 1 | — |
| dice | 0.5 | — |
| ra | 0.5 | — |

`RequestsPerDay = null` means no daily cap at the rate-limiter level (`PerSourceDailyBudget` above still applies).

Do not raise Ticketmaster's `req/s` above 5 — their API rejects spikes. Lower values are always safe.

---

## Database location

The plugin stores its own SQLite database under Jellyfin's data path:

```
/var/lib/jellyfin/data/concertradar/concerts.db
```

Tables: `concerts`, `artists`, `source_state`, `schema_version`. Never write to this database manually — migrations run on every startup and expect exclusive control.

Backup advice: include this directory in your normal Jellyfin backup. It's cheap to rebuild (one daily run) if lost.

---

## Admin actions

### Run now
Queues the scheduled task immediately. Safe to call while a run is in progress — the task manager serializes executions.

### Reset source
Clears the circuit breaker for a specific source (resets `consecutive_errors`, clears `disabled_until`, status → `Ok`). Use after fixing an upstream issue or rotating credentials.

### Purge all
Deletes every concert row. Forces a full re-fetch on the next run. Useful after changing filters dramatically.

### Resolve IDs
Clears all cached `ext_ids` so MusicBrainz URL relationships are re-resolved on the next run. Needed after upgrading to a plugin version that adds a new source or changes ID extraction.

---

## Troubleshooting

**Nothing is fetched on the first run.** Verify at least one source is enabled AND has credentials (where required). Check the admin status card for per-source error messages.

**All sources in `Failing` state.** Circuit breaker tripped repeatedly. Look at `LastError` on the status card, resolve the underlying issue (bad credential, Cloudflare block, network outage), and click **Reset source** per source.

**Events from wrong city appear.** Location filter uses haversine distance when lat/lon are set; otherwise falls back to city/country equality. Add lat/lon to your `LocationFilter` to tighten.

**Scheduled task runs but admin status shows nothing.** Most likely the page loaded before migration finished. Refresh the admin page after a few seconds. The repositories block until the migration gate signals ready, so data is consistent once available.

**Dice returns 403 immediately.** Cloudflare flagged the scraper. The circuit breaker disables Dice automatically; retry next day. If persistent, disable Dice.

**Audit log / change tracking.** The plugin logs per-run summaries to the Jellyfin log (`Information` for start/end, `Warning` for retries, `Error` for circuit trips). `journalctl -u jellyfin -f | grep ConcertRadar` is a quick filter.
