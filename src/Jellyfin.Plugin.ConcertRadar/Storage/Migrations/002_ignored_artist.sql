-- Migration 002 — admin-ignored artists (non-null = ignored; see SPEC.md "Ignored artists")
ALTER TABLE artists ADD COLUMN ignored_at TEXT;
