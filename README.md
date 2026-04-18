# Jellyfin Concert Radar

Jellyfin server plugin that aggregates upcoming concerts for artists in your music library from multiple sources, normalizes them, and surfaces them in an admin dashboard and a user-accessible concert list.

## Status

Early development (0.1.0). See `SPEC.md` for the full specification, `TASKS.md` for the implementation backlog, and `TESTS.md` for the test plan.

## Target

- Jellyfin 10.11.x
- .NET 9

## Sources

| Source | Access | Requires | Notes |
|---|---|---|---|
| Ticketmaster | Official API | Free API key | 5000 req/day, 5 req/s |
| Bandsintown | Official API | Self-assigned `app_id` | MBID-native artist identifier |
| EdmTrain | Official API | API key (manual approval) | EDM-only; attribution required |
| Songkick | Web scrape | — | SSR HTML; stable selectors |
| Dice.fm | Web scrape | ToS opt-in | Next.js `__NEXT_DATA__` extraction; Cloudflare-sensitive |
| Resident Advisor | GraphQL scrape | ToS opt-in | Reverse-engineered; default OFF |

Dice and Resident Advisor rely on non-public methods. Enable only after reviewing each source's terms of service for your use case.

## Install

### From a plugin repository (recommended)

1. Open **Jellyfin Dashboard → Plugins → Repositories → +**.
2. Add a repository pointing at the `manifest.json` hosted with the release (URL published on the GitHub releases page).
3. Switch to the **Catalog** tab and install **Concert Radar**.
4. Restart Jellyfin when prompted.

### Manual install

1. Download the latest `jellyfin-concert-radar-<version>.zip` from the GitHub releases page.
2. Extract into `/var/lib/jellyfin/plugins/ConcertRadar_<version>/` on the Jellyfin server.
3. Restart Jellyfin.

## Configure

Dashboard → Plugins → **Concert Radar** opens the admin page. Configuration is server-global.

1. **Credentials** — paste API keys for Ticketmaster, Bandsintown, and EdmTrain (leave blank to disable a source).
2. **Sources** — tick the sources you want. Dice and RA also require the ToS opt-in checkbox before they can run.
3. **Filters** — add one or more locations (city, country, lat/lon, radius), country allowlist, genre allowlist, date window, skip-festival, skip-sold-out.
4. **Scheduler** — `Max artists per run` controls the round-robin batch size. `Per-source daily budget` caps the API calls per source per day. `Stale record days` controls the GC threshold for events that haven't been re-seen.
5. **Rate limits** — adjust per-source req/s and req/day overrides.

Click **Save**. The first scheduled run happens at the next daily trigger (03:00 local by default). Hit **Run now** to trigger immediately.

## User concert list

After installation, any logged-in Jellyfin user can bookmark:

    https://<your-jellyfin>/web/ConfigurationPage?name=concertradar-view

This page is independent from the admin Dashboard and does not require admin privileges. Configuration remains admin-only.

The page filters by date range, country, city, source, and artist, with sort by date / artist / city. Each concert card links out to the source event page (`View on Source`) and, when distinct, to a direct ticket URL.

## Architecture

Two-phase:
- **Ingest** (scheduled task, daily): enumerates music-library artists → resolves MusicBrainz URL relationships → queries each enabled source through rate-limited adapters → normalizes the results → upserts into a private SQLite database.
- **Surface**: admin + user views call the plugin's own REST endpoints under `/Plugins/ConcertRadar/api/`.

Source adapters share a common `ISourceAdapter` contract. A circuit breaker disables a source after repeated failures and re-enables it after a cooldown. The scheduler runs a round-robin over artists so every artist is eventually refreshed without overloading any source.

See `SPEC.md` for the full architecture, schema, and adapter contract.

## Known limitations

- No per-user configuration (Jellyfin doesn't support per-user plugin config natively).
- No in-app nav entry for the user view — users bookmark the URL. Stock Jellyfin has no API for adding a user-facing page to the main menu.
- Ticketmaster / Bandsintown / EdmTrain terms of service permit non-commercial use only. Check each source's terms for your deployment.
- Songkick, Dice, and Resident Advisor are scraped. Their HTML / JSON shapes can change; the plugin's contract tests catch drift in CI, but breakage can still happen between releases.
- Cloudflare on Dice may block scrapes with HTTP 403. The circuit breaker disables the source; re-enable from the admin page after the block lifts.

## Develop

```bash
# Requires .NET 9 SDK
dotnet restore
dotnet build --configuration Release
dotnet test --configuration Release
```

Tests use temp-file SQLite and stub HTTP handlers — no live network calls. Fixtures live under `tests/fixtures/`.

Sub-agent definitions for Claude Code are in `../.claude/agents/` (backend, frontend, tests).

## License

MIT — see `LICENSE`.
