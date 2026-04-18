-- Migration 001 — initial schema
-- All statements use IF NOT EXISTS so this migration is idempotent.

CREATE TABLE IF NOT EXISTS schema_version (
    version    INTEGER PRIMARY KEY,
    applied_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS concerts (
    id              TEXT PRIMARY KEY,
    source          TEXT NOT NULL,
    source_event_id TEXT NOT NULL,
    source_url      TEXT NOT NULL,
    artist_mbid     TEXT,
    artist_name     TEXT NOT NULL,
    event_datetime  TEXT NOT NULL,
    venue_name      TEXT,
    venue_address   TEXT,
    city            TEXT,
    region          TEXT,
    country         TEXT,
    lat             REAL,
    lon             REAL,
    lineup          TEXT,
    ticket_url      TEXT,
    price_min       REAL,
    price_max       REAL,
    currency        TEXT,
    onsale_at       TEXT,
    fetched_at      TEXT NOT NULL,
    last_seen_at    TEXT NOT NULL,
    UNIQUE(source, source_event_id)
);

CREATE INDEX IF NOT EXISTS ix_concerts_datetime    ON concerts(event_datetime);
CREATE INDEX IF NOT EXISTS ix_concerts_artist_date ON concerts(artist_mbid, event_datetime);
CREATE INDEX IF NOT EXISTS ix_concerts_geo_date    ON concerts(country, city, event_datetime);

CREATE TABLE IF NOT EXISTS artists (
    id                 TEXT PRIMARY KEY,
    mbid               TEXT,
    name               TEXT NOT NULL,
    jellyfin_item_id   TEXT,
    ext_ids            TEXT NOT NULL DEFAULT '{}',
    last_checked_at    TEXT,
    last_error         TEXT,
    consecutive_errors INTEGER NOT NULL DEFAULT 0
);

CREATE INDEX IF NOT EXISTS ix_artists_last_checked ON artists(last_checked_at);

CREATE TABLE IF NOT EXISTS source_state (
    source             TEXT PRIMARY KEY,
    status             TEXT NOT NULL,
    consecutive_errors INTEGER NOT NULL DEFAULT 0,
    disabled_until     TEXT,
    calls_today        INTEGER NOT NULL DEFAULT 0,
    calls_today_reset  TEXT,
    next_allowed_at    TEXT,
    last_error         TEXT,
    last_success_at    TEXT
);
