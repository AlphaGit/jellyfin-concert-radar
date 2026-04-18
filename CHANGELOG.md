# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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

## [0.1.0] — TBD

Initial release. Nothing to see yet.
