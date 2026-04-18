# Test Fixtures

Recorded HTTP response bodies and HTML snapshots used by contract and unit tests.
**No API keys, session tokens, or PII may appear in any committed fixture.**

---

## Directory layout

```
tests/fixtures/
  musicbrainz/
    search_multi_score.json         – artist search: 3 candidates, scores 95/70/50
    search_all_low_score.json       – artist search: all scores below threshold (60/45)
    artist_with_songkick_rel.json   – artist URL-rels: Songkick relation present
    artist_with_dice_ra_rels.json   – artist URL-rels: Dice + RA relations present
    artist_with_bandsintown_rel.json – artist URL-rels: Bandsintown relation present
    artist_unknown_rels.json        – artist URL-rels: only unknown relation types
  ticketmaster/
    happy_path.json                 – events search, 5 events
    empty_results.json              – events search, _embedded absent
    rate_limited.json               – 429 body
    error_5xx.json                  – 503 body
  bandsintown/
    happy_path.json
    empty_results.json
  edmtrain/
    happy_path.json
    empty_results.json
  songkick/
    happy_path.html
    empty_results.html
  dice/
    happy_path.html                 – page with __NEXT_DATA__ blob
    missing_next_data.html          – page without __NEXT_DATA__
  ra/
    happy_path.json                 – GraphQL eventListings response
```

---

## Capturing a fresh fixture

Replace `<API_KEY>` and `<MBID>` with real values. Never commit the curl command
with real credentials — only commit the scrubbed output.

### MusicBrainz artist search

```bash
curl -s -A "JellyfinConcertRadar/0.1.0 ( https://github.com/alphagma/jellyfin-concert-radar )" \
  "https://musicbrainz.org/ws/2/artist?query=artist:%22Radiohead%22&fmt=json" \
  | jq . > tests/fixtures/musicbrainz/search_multi_score.json
```

### MusicBrainz artist URL-rels

```bash
curl -s -A "JellyfinConcertRadar/0.1.0 ( https://github.com/alphagma/jellyfin-concert-radar )" \
  "https://musicbrainz.org/ws/2/artist/a74b1b7f-71a5-4011-9441-d0b5e4122711?inc=url-rels&fmt=json" \
  | jq . > tests/fixtures/musicbrainz/artist_with_songkick_rel.json
```

### Ticketmaster events

```bash
curl -s \
  "https://app.ticketmaster.com/discovery/v2/events.json?apikey=<API_KEY>&keyword=Radiohead&countryCode=US" \
  > tests/fixtures/ticketmaster/happy_path.json
```

---

## Scrubbing secrets

After capturing, remove API keys and personal data before committing.

### Remove API key from query strings (jq, JSON fixtures)

```bash
jq 'del(.. | .apikey? // empty)' fixture.json > fixture.scrubbed.json
mv fixture.scrubbed.json fixture.json
```

### Remove session tokens / auth headers (sed, general)

```bash
sed -i '' 's/"access_token":"[^"]*"/"access_token":"REDACTED"/g' fixture.json
```

### Verify no secrets remain before committing

```bash
grep -rn "apikey\|api_key\|access_token\|Authorization" tests/fixtures/
```

---

## When to refresh

- A source changes its JSON or HTML shape and a contract test fails.
- A new field is added that the adapter needs to parse.
- A fixture is older than 6 months (shape drift risk).

**Do not suppress a failing contract test — refresh the fixture or update the parser.**
