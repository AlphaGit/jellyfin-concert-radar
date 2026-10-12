# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.2.0] — 2026-10-11

### Upgrading from 0.1.x — once

- **Requires Jellyfin 12.** This version does not load on Jellyfin 10.11. Jellyfin 12 disables
  0.1.x during its own upgrade; your settings and stored concerts are kept and this version reads
  them unchanged.
- **Replace the repository address.** The catalogue is no longer served from GitHub Pages. In
  Dashboard → Plugins → Repositories, add
  `https://raw.githubusercontent.com/AlphaGit/jellyfin-concert-radar/main/repo/manifest.json` and
  remove the old `github.io` address.

### Changed

- **Runs on Jellyfin 12** (.NET 10). Built against the Jellyfin 12.0.0 libraries, so it loads on
  every 12.x server.
- **The Concerts menu entry registers through Plugin Pages' own API** (Plugin Pages 3.0.0.0 or
  later, with File Transformation) and is withdrawn when the server stops. On first start the
  plugin removes the entry 0.1.x wrote into Plugin Pages' `config.json`, so an uninstall no longer
  leaves a dead menu entry behind.
- **API routes follow Jellyfin's own naming.** `Plugins/ConcertRadar/api/concerts` is now
  `Plugins/ConcertRadar/Concerts`; every segment is PascalCase and the `api` segment is gone (see
  `docs/http-surface.md`). Responses are always camelCase, whatever the host default. A script
  calling the old paths must be updated.
- **The catalogue name is "Concert Radar"**, matching the name the plugin reports, so Jellyfin
  replaces an older copy on upgrade instead of loading both.

## [0.1.1] — 2026-04-19

### Fixed
- Saving plugin settings no longer clears configured Locations or Rate Limits. The admin form previously serialized nested `LocationFilter` and `SourceRateLimitEntry` objects with camelCase keys, which the XML-backed configuration deserializer dropped to defaults.
- Stale empty `LocationFilter` / `SourceRateLimitEntry` rows left behind by the above bug are pruned once at startup.

### Added
- Nominatim-backed geocoding search in the admin Locations panel — typing a city name suggests up to 5 OSM matches; selecting one stores a chip with resolved city/region/country and coordinates. The previous table remains as an "Advanced" disclosure for manual entry.
- `JellyfinArtistId` on the concert DTO — the user-facing concerts view now links each artist name to the local Jellyfin artist page whenever the concert's MBID resolves to a library artist.

## [0.1.0] — Initial release

### Added
- Source adapters for Ticketmaster, Bandsintown, EdmTrain (official APIs).
- Scrape adapters for Songkick, Dice.fm, Resident Advisor (Dice and RA default off, ToS-gated).
- `RefreshConcertsTask` — daily scheduled task with round-robin artist scheduling, per-source daily budget, and circuit breaker.
- SQLite storage for concerts, artists, and per-source state.
- MusicBrainz resolver — MBID search + URL-relationship extraction for Songkick, Bandsintown, Dice, RA external ids.
- Host-keyed rate limiter built on `System.Threading.RateLimiting` with Retry-After support.
- Admin page (Dashboard) with status, credentials, sources, filters, scheduler, and rate-limits sections.
- User-accessible concerts view at `/web/ConfigurationPage?name=concertradar-view`.
- REST API under `/Plugins/ConcertRadar/api/` — user reads (`[Authorize]`) and admin writes (`[Authorize(Policy = RequiresElevation)]`).
- JPRM build manifest and GitHub Actions packaging workflow.
- Test suite: 135 unit + integration + contract tests.
- Plugin Pages integration — registers a `Concerts` entry in the web client hamburger drawer for all users via the IAmParadox27 Plugin Pages framework; seeds the registration on every startup if Plugin Pages is installed.
- Month-grouped user concerts view with daybox card layout, filter bar, sort bar, and pagination.
- Next-run computation on the admin status endpoint (derives from configured task triggers).
- Per-source-defaults fallback on the rate limits table + defensive filtering of corrupted entries.
- `MANUAL.md` — end-to-end admin + user walkthrough with screenshots.

