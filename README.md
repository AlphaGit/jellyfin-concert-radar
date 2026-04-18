# Jellyfin Concert Radar

Jellyfin server plugin that aggregates upcoming concerts for the artists in your music library from multiple sources (Ticketmaster, Bandsintown, EdmTrain, Songkick, Dice.fm, Resident Advisor), normalizes them, and surfaces them in an admin dashboard and a user-accessible concert list.

## Status

Early development. See `SPEC.md` for the full specification, `TASKS.md` for the implementation backlog, and `TESTS.md` for the test plan.

## Target

- Jellyfin 10.11.x
- .NET 9

## User concert list

After installing the plugin, any logged-in Jellyfin user can bookmark:

    https://<your-jellyfin>/web/ConfigurationPage?name=concertradar-view

This page is independent from the admin Dashboard and does not require
admin privileges. Configuration remains admin-only.

## License

MIT — see `LICENSE`.
