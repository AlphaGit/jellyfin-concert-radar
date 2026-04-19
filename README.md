# Jellyfin Concert Radar

Jellyfin server plugin that aggregates upcoming concerts for every artist in your music library from multiple external sources, normalizes them into a single canonical schema, and surfaces them in both an admin dashboard and a user-accessible concert list.

The plugin runs a scheduled task that round-robins your artists through six data sources (three official APIs, three opt-in web scrapers), stores normalized events in a private SQLite database, and exposes them via REST endpoints consumed by the embedded admin and user views. Each event keeps a direct link back to its source page (and, when distinct, a buy-tickets link) so operators and users can jump straight to purchase.

## Status

Early development (0.1.0). See `SPEC.md` for the full specification, `TASKS.md` for the implementation backlog, `TESTS.md` for the test plan, and `CONFIGURATION.md` for a deep-dive on every setting.

## Target

- Jellyfin 10.11.x (production confirmed against 10.11.6)
- .NET 9

## Sources

| Source | Access | Requires | Notes |
|---|---|---|---|
| Ticketmaster | Official API | Free API key | 5000 req/day, 5 req/s |
| Bandsintown | Official API | Self-assigned `app_id` | MBID-native artist identifier |
| EdmTrain | Official API | API key (manual approval) | EDM-only; attribution required |
| Songkick | Web scrape | ToS opt-in | SSR HTML; stable selectors |
| Dice.fm | Web scrape | ToS opt-in | Next.js `__NEXT_DATA__` extraction; Cloudflare-sensitive |
| Resident Advisor | GraphQL scrape | ToS opt-in | Reverse-engineered; default OFF |

Scrape adapters (Songkick, Dice, Resident Advisor) rely on non-public methods. Enable only after reviewing each source's terms of service for your deployment.

## Build from source

Requirements:
- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) (9.0.115 or later).
- `git` and a POSIX shell.
- Optional for release packaging: Python 3.10+ and [`jprm`](https://github.com/oddstr13/jellyfin-plugin-repository-manager) (`pip install jprm`).

macOS (keg-only Homebrew formula):

```bash
brew install dotnet@9
export DOTNET_ROOT="$(brew --prefix)/opt/dotnet@9/libexec"
export PATH="$(brew --prefix)/opt/dotnet@9/bin:$PATH"
```

Clone, restore, build, test:

```bash
git clone https://github.com/<owner>/jellyfin-concert-radar.git
cd jellyfin-concert-radar
dotnet restore
dotnet build --configuration Release
dotnet test  --configuration Release
```

The plugin DLL lands at:

```
src/Jellyfin.Plugin.ConcertRadar/bin/Release/net9.0/Jellyfin.Plugin.ConcertRadar.dll
```

To produce a release-ready zip (same format the manifest-based install consumes):

```bash
pip install jprm
jprm plugin build . --version 0.1.0 --output ./artifacts
ls ./artifacts   # jellyfin-concert-radar_0.1.0.zip
```

## Deploy

### Manual install (simplest)

1. Produce or download `jellyfin-concert-radar_<version>.zip`.
2. On the Jellyfin server, extract the DLL into the plugins directory:
   ```bash
   mkdir -p /var/lib/jellyfin/plugins/ConcertRadar_<version>/
   unzip jellyfin-concert-radar_<version>.zip -d /var/lib/jellyfin/plugins/ConcertRadar_<version>/
   chown -R jellyfin:jellyfin /var/lib/jellyfin/plugins/ConcertRadar_<version>/
   systemctl restart jellyfin
   ```
3. Open **Dashboard → Plugins** and confirm **Concert Radar** is listed.

### From a plugin repository (recommended once released)

1. **Dashboard → Plugins → Repositories → Add**.
2. Enter the repository name and the `manifest.json` URL published alongside each release (GitHub Pages of this repository once CI wires it up).
3. **Catalog** → find **Concert Radar** → **Install** → restart Jellyfin when prompted.

### Upgrade

1. Stop Jellyfin.
2. Replace the DLL in `/var/lib/jellyfin/plugins/ConcertRadar_<version>/`. The SQLite database at `/var/lib/jellyfin/data/concertradar/concerts.db` carries across versions; migrations run automatically on startup.
3. Start Jellyfin.

### Uninstall

1. Stop Jellyfin.
2. Delete `/var/lib/jellyfin/plugins/ConcertRadar_*/`.
3. Optional: delete the plugin's data directory `/var/lib/jellyfin/data/concertradar/` to remove cached concerts.
4. Start Jellyfin.

## Configure

See `CONFIGURATION.md` for the full settings reference with defaults, allowed ranges, and trade-offs. Short version:

1. Open **Dashboard → Plugins → Concert Radar**.
2. Enter API keys for the sources you want to use.
3. Tick the matching source toggles; accept the ToS checkbox for any scrape source.
4. Add at least one **Location** (city + country + radius) so filtering is meaningful.
5. Save, then hit **Run now** — or wait for the daily 03:00 trigger.

## User concert list

After installation, any logged-in Jellyfin user can bookmark:

```
https://<your-jellyfin>/web/ConfigurationPage?name=concertradar-view
```

This page is independent from the admin Dashboard and does not require admin privileges. Configuration remains admin-only. The page filters by date range, country, city, source, and artist, with sort by date / artist / city. Each concert card links out to the source event page (`View on Source`) and, when distinct, to a direct ticket URL.

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

Tests use temp-file SQLite and stub HTTP handlers — no live network calls. Fixtures live under `tests/fixtures/`.

Sub-agent definitions for Claude Code are in `../.claude/agents/` (backend, frontend, tests).

## License

MIT — see `LICENSE`.
