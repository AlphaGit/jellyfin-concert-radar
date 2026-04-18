# Jellyfin Concert Radar — Test Plan

Test pyramid:

- **Unit** — pure functions, DTO mapping, filter logic, per-adapter response parsing with recorded fixtures. `xUnit + FluentAssertions + NSubstitute`. No network, no DB file.
- **Integration** — SQLite repositories against a real file-backed temp DB; scheduled task end-to-end against mock adapters.
- **Contract** — per-source fixture parsing (recorded HTTP bodies + HTML snapshots checked into `tests/fixtures/<source>/...`). Catches upstream drift.
- **Smoke (manual)** — install built plugin into a local Jellyfin 10.11.6, run scheduled task, inspect admin UI + user view. Documented steps, not automated.

Fixtures directory layout:
```
tests/
  Jellyfin.Plugin.ConcertRadar.Tests/
    Fixtures/
      ticketmaster/events_radiohead_us.json
      ticketmaster/rate_limit_headers.txt
      bandsintown/radiohead_by_mbid.json
      edmtrain/events_deadmau5.json
      songkick/artist_page_radiohead.html
      dice/artist_page_nextdata.html
      ra/graphql_eventlistings.json
      musicbrainz/artist_mbid_url_rels.xml
```

Each source fixture has at least: `happy_path`, `empty_results`, `malformed`, `rate_limited`, `error_5xx`.

---

## Unit tests

### Configuration
- `PluginConfigurationDefaults_MatchSpec` — every default value per SPEC §5.
- `PluginConfiguration_RoundTripsAsXml` — serialize and deserialize; all collections survive.

### Normalizer
- `Normalize_MapsAllRequiredFields` — valid `RawEvent` → `ConcertRecord`, all mandatory fields populated.
- `Normalize_RejectsMissingSourceUrl` — throws `ValidationException`.
- `Normalize_RejectsMissingEventDateTime`.
- `Normalize_ParsesIsoDateTimeWithOffset`.
- `Normalize_ParsesDateTimeWithZuluSuffix`.
- `Normalize_AppliesCountryAllowlistWhenSourceDidntFilter`.
- `Normalize_AppliesMinMaxDaysAhead`.
- `Normalize_AppliesSkipFestivals`.
- `Normalize_AppliesSkipSoldOut`.
- `Normalize_PreservesLineupOrder`.
- `Normalize_GeneratesStableIdFromSourcePlusEventId` — same input → same id (idempotent upserts).

### Rate limiter
- `HostRateLimiter_AllowsUpToRps` — 5 rps config allows 5 immediate acquires.
- `HostRateLimiter_ThrottlesBeyondRps` — 6th acquire waits ≥~200ms.
- `HostRateLimiter_DailyBudgetStopsAcquires` — budget exhausted returns false.
- `HostRateLimiter_DailyBudgetResetsAtMidnightUtc`.
- `HostRateLimiter_HonorsBackoffUntil`.

### Circuit breaker (`SourceStateRepository` pure logic)
- `RecordFailure_OpensCircuitAfterThreshold`.
- `RecordSuccess_ClosesCircuitAndResetsCounter`.
- `IsOpen_ReturnsTrueUntilDisabledUntilPasses`.
- `Reset_ClearsStateImmediately`.

### Library enumerator
- `LibraryArtistEnumerator_YieldsMbidFromProviderIds`.
- `LibraryArtistEnumerator_SplitsCommaSeparatedMbids_TakesFirst`.
- `LibraryArtistEnumerator_YieldsNullMbidWhenAbsent`.

### Scheduler ordering
- `ArtistRepository_GetNextBatch_OrdersByLastCheckedAscNullsFirst`.
- `ArtistRepository_GetNextBatch_RespectsLimit`.
- `ArtistRepository_UpdateChecked_AdvancesTimestamp`.
- `ArtistRepository_UpdateChecked_WhenAllSourcesFailed_DoesNotAdvance` (this logic lives in the scheduler; verify in scheduler test below).

### MusicBrainz resolver
- `MbResolver_ParsesUrlRels_ExtractsSongkickId` — given fixture XML, returns `{songkick: "253846"}`.
- `MbResolver_ParsesUrlRels_ExtractsRaSlug`.
- `MbResolver_IgnoresUnknownUrlTypes`.
- `MbResolver_ResolveByName_PicksHighestScore`.
- `MbResolver_ResolveByName_ReturnsNullBelowThreshold`.

### Adapter: Ticketmaster
- `TicketmasterAdapter_ParsesHappyPath_ExtractsFiveEvents` — fixture `events_radiohead_us.json`.
- `TicketmasterAdapter_ParsesPriceRange`.
- `TicketmasterAdapter_ParsesOnSaleDate`.
- `TicketmasterAdapter_MapsAttractionIdToCache`.
- `TicketmasterAdapter_EmitsEmptyForNoResults`.
- `TicketmasterAdapter_HandlesRateLimitHeaders_RecordsQuota`.
- `TicketmasterAdapter_Retries_On429`.
- `TicketmasterAdapter_OpensCircuit_AfterRepeated5xx`.

### Adapter: Bandsintown
- `BandsintownAdapter_UsesMbidIdentifierWhenPresent`.
- `BandsintownAdapter_FallsBackToUrlEncodedName`.
- `BandsintownAdapter_ParsesEventsArray`.
- `BandsintownAdapter_MapsVenueFields`.
- `BandsintownAdapter_FiltersByLocationClientSide`.

### Adapter: EdmTrain
- `EdmTrainAdapter_ResolvesArtistIdOnce_Cached`.
- `EdmTrainAdapter_ParsesEventFields`.
- `EdmTrainAdapter_HandlesFestivalFlag`.

### Adapter: Songkick scrape
- `SongkickAdapter_ParsesUpcomingEventsFromArtistPage` — fixture `artist_page_radiohead.html`.
- `SongkickAdapter_ExtractsDateFromTimeDatetimeAttribute`.
- `SongkickAdapter_ExtractsVenueCityCountry`.
- `SongkickAdapter_EmitsSourceUrlPerEvent`.
- `SongkickAdapter_HandlesEmptyUpcomingList`.
- `SongkickAdapter_FlagsDegradedWhenSelectorsMiss`.

### Adapter: Dice.fm scrape
- `DiceAdapter_ExtractsNextDataBlob`.
- `DiceAdapter_ParsesEventsFromNextData`.
- `DiceAdapter_HandlesMissingNextData` — malformed fixture.
- `DiceAdapter_TriggersCircuitOnRepeated403`.
- `DiceAdapter_SkippedWhenAcceptDiceScrapeTosFalse`.

### Adapter: Resident Advisor scrape
- `RaAdapter_ConstructsGraphqlQuery`.
- `RaAdapter_ParsesEventListingsResponse`.
- `RaAdapter_SkippedWhenAcceptRaScrapeTosFalse`.
- `RaAdapter_ResolvesArtistSlugFromCache`.

---

## Integration tests

### `ConcertRepository` (SQLite, temp file)
- `Upsert_InsertsNewRecord`.
- `Upsert_UpdatesExistingBySourceAndSourceEventId`.
- `Upsert_DoesNotDuplicateAcrossSources` — same `source_event_id` in different sources are distinct rows.
- `Query_FiltersByDateRange`.
- `Query_FiltersByCountry`.
- `Query_FiltersByCity`.
- `Query_FiltersBySource`.
- `Query_FiltersByArtistMbid`.
- `Query_PaginatesCorrectly`.
- `Query_SortsByDate_AscDefault`.
- `DeletePast_RemovesOnlyPastEvents`.
- `DeleteStale_RemovesOnlyBeyondThreshold`.
- `PurgeAll_EmptiesTable`.

### `ArtistRepository`
- `UpsertFromLibrary_InsertsNewArtists`.
- `UpsertFromLibrary_UpdatesNameAndMbid_DoesNotDelete`.
- `GetNextBatch_Returns500InLimit50` → 50 rows, oldest first.
- `GetNextBatch_NewArtistsJumpToFront`.
- `UpdateChecked_SetsTimestampAndClearsError`.
- `SetExternalIds_MergesWithExisting`.
- `ResetAllChecked_NullifiesTimestamps`.

### `SchemaMigrator`
- `Migrator_AppliesFirstMigration_OnEmptyDb`.
- `Migrator_IsIdempotent` — second run is a no-op.
- `Migrator_TracksVersionInSchemaVersionTable`.

### `RefreshConcertsTask` (end-to-end with mock adapters + real SQLite)
- `Task_ProcessesBatchSize_MaxArtistsPerRun`.
- `Task_UpsertsConcerts_AcrossEnabledSources`.
- `Task_SkipsDisabledSources`.
- `Task_SkipsSourcesWithOpenCircuit`.
- `Task_AdvancesLastCheckedAt_OnPartialFailure`.
- `Task_DoesNotAdvance_WhenAllSourcesFail`.
- `Task_GcRemovesPastEvents`.
- `Task_GcRemovesStaleEvents`.
- `Task_RespectsPerSourceDailyBudget`.
- `Task_ReportsProgressFromZeroTo100`.
- `Task_IsCancellable`.

### API controllers
- `ConcertsController_GetConcerts_RequiresAuthorizedUser`.
- `ConcertsController_GetConcerts_ReturnsExpectedDtos`.
- `ConcertsController_GetConcerts_PaginatesAndSorts`.
- `AdminController_RunNow_RequiresElevation`.
- `AdminController_RunNow_ReturnsAccepted_SchedulesTask`.
- `AdminController_Purge_EmptiesRepo`.
- `AdminController_ResetSource_ClearsCircuit`.
- `AdminController_ResolveIds_ClearsExtIds`.

---

## Contract / fixture tests

For each source (Ticketmaster, Bandsintown, EdmTrain, Songkick, Dice, RA): a `_ContractTests` suite that runs the adapter's parser against every fixture file in `tests/fixtures/<source>/` and asserts it doesn't throw + at least one `RawEvent` is produced on happy-path fixtures. These are the drift detectors — when a source changes its HTML or JSON shape, these break first.

Additional contract assertions:
- `Ticketmaster_FixtureMatchesDocumentedSchema` — verifies fields used in parser exist in fixture.
- `Songkick_DomSelectorsHit_OnFixture` — selectors `.event-listing`, `time[datetime]` each match ≥1 element.
- `Dice_NextDataBlob_ContainsExpectedShape` — `props.pageProps.events` (or current path) resolves.
- `Ra_GraphqlResponseShape_MatchesQuery` — non-null `data.eventListings.data`.

---

## Frontend tests

Keep minimal (embedded HTML + vanilla JS). No React/Vue build pipeline.

### Admin UI (`admin.html`)
- **Manual smoke**: loads inside Dashboard, reads config, saves config, round-trips without loss.
- **Manual smoke**: status auto-refreshes every 5s; "Run now" triggers task; "Reset source" clears circuit.
- **Manual smoke**: invalid inputs (e.g. negative `MaxArtistsPerRun`) show inline validation.

### User view (`view.html`)
- **Manual smoke**: direct URL visit while logged in shows concerts; logged-out redirects to login.
- **Manual smoke**: filters narrow results; pagination advances; links out to `source_url` open in new tab.
- **Manual smoke**: empty state shown when no concerts match filters.

Optional automated: Playwright tests for admin + view pages, driven against a local Jellyfin container. Deferred unless stability becomes an issue.

---

## Coverage & gating

- CI gates PRs on: `dotnet build` success, `dotnet test` green, no new warnings as errors.
- Target ≥80% line coverage on `Sources/`, `Normalization/`, `Storage/`, `ScheduledTasks/`.
- Fixtures are hand-curated and committed; refresh procedure documented in a `Fixtures/README.md` (how to re-capture a live response and scrub any API keys).

## Smoke test checklist (manual, pre-release)

Performed against a real Jellyfin 10.11.6 instance with a small music library.

1. Install plugin via manual DLL drop.
2. Restart Jellyfin; confirm plugin appears in Dashboard → Plugins.
3. Configure Ticketmaster API key + one location; save.
4. Hit "Run now"; wait for completion; confirm logs show artists processed and events upserted.
5. Open user view URL while logged in; confirm concerts list renders with correct data.
6. Click "View on Source" → opens Ticketmaster page in new tab.
7. Enable Bandsintown; run again; confirm rows from both sources appear and are deduplicated per `(source, source_event_id)`.
8. Enable Dice (with ToS opt-in); confirm circuit stays closed under normal operation; pull the plug (invalid UA) to confirm circuit opens and admin UI reports `Failing`.
9. Change `MaxArtistsPerRun` to a small number; confirm round-robin behavior over multiple runs.
10. Restart Jellyfin; confirm data persists; next scheduled run picks up where it left off.
