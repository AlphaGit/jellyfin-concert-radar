using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ConcertRadar.Configuration;
using Jellyfin.Plugin.ConcertRadar.Library;
using Jellyfin.Plugin.ConcertRadar.Model;
using Jellyfin.Plugin.ConcertRadar.Normalization;
using Jellyfin.Plugin.ConcertRadar.Resolution;
using Jellyfin.Plugin.ConcertRadar.Sources;
using Jellyfin.Plugin.ConcertRadar.Storage;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ConcertRadar.ScheduledTasks;

/// <summary>
/// Scheduled task that enumerates library artists, fetches upcoming concerts from all
/// enabled source adapters, normalizes the results, and stores them in SQLite.
/// </summary>
public sealed class RefreshConcertsTask : IScheduledTask
{
    private readonly LibraryArtistEnumerator _artistEnumerator;
    private readonly ArtistRepository _artistRepository;
    private readonly ConcertRepository _concertRepository;
    private readonly SourceStateRepository _sourceStateRepository;
    private readonly MusicBrainzResolver _mbResolver;
    private readonly IEnumerable<ISourceAdapter> _adapters;
    private readonly TimeProvider _clock;
    private readonly ILogger<RefreshConcertsTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RefreshConcertsTask"/> class.
    /// </summary>
    /// <param name="artistEnumerator">Library artist enumerator.</param>
    /// <param name="artistRepository">Artist repository.</param>
    /// <param name="concertRepository">Concert repository.</param>
    /// <param name="sourceStateRepository">Source state repository.</param>
    /// <param name="mbResolver">MusicBrainz external-ID resolver.</param>
    /// <param name="adapters">All registered source adapters.</param>
    /// <param name="clock">Time provider.</param>
    /// <param name="logger">Logger.</param>
    public RefreshConcertsTask(
        LibraryArtistEnumerator artistEnumerator,
        ArtistRepository artistRepository,
        ConcertRepository concertRepository,
        SourceStateRepository sourceStateRepository,
        MusicBrainzResolver mbResolver,
        IEnumerable<ISourceAdapter> adapters,
        TimeProvider clock,
        ILogger<RefreshConcertsTask> logger)
    {
        _artistEnumerator       = artistEnumerator;
        _artistRepository       = artistRepository;
        _concertRepository      = concertRepository;
        _sourceStateRepository  = sourceStateRepository;
        _mbResolver             = mbResolver;
        _adapters               = adapters;
        _clock                  = clock;
        _logger                 = logger;
    }

    /// <inheritdoc />
    public string Name => "Refresh concert radar";

    /// <inheritdoc />
    public string Key => "ConcertRadar.Refresh";

    /// <inheritdoc />
    public string Description => "Fetches upcoming concerts for library artists from all enabled sources.";

    /// <inheritdoc />
    public string Category => "Library";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.DailyTrigger,
            TimeOfDayTicks = TimeSpan.FromHours(3).Ticks,
        };
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var cfg = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var now = _clock.GetUtcNow();

        _logger.LogInformation("RefreshConcertsTask: starting run at {Now}.", now);
        progress.Report(0);

        // Step 1: sync library → artists table.
        var libraryArtists = _artistEnumerator.Enumerate();
        await _artistRepository.UpsertFromLibraryAsync(libraryArtists, cancellationToken)
            .ConfigureAwait(false);

        // Step 2: pull the next batch of artists ordered by last_checked_at ASC NULLS FIRST.
        var batch = await _artistRepository
            .GetNextBatchAsync(cfg.MaxArtistsPerRun, cancellationToken)
            .ConfigureAwait(false);

        int batchCount = batch.Count;
        _logger.LogInformation("RefreshConcertsTask: processing {Count} artists.", batchCount);

        // Build a SourceFilter from current config.
        var sourceFilter = BuildSourceFilter(cfg, now);

        int processed = 0;
        int totalUpserts = 0;
        int totalErrors = 0;

        var adapters = _adapters.ToList();

        // Step 3: per-artist processing.
        foreach (var artist in batch)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Ensure external IDs are resolved (one-time via MusicBrainz).
            // freshExtIds holds the just-resolved dict so adapters use it immediately.
            IReadOnlyDictionary<string, string>? freshExtIds = null;

            if (!artist.ExternalIds.ContainsKey(MusicBrainzResolver.MbResolvedSentinel) &&
                !string.IsNullOrEmpty(artist.Mbid))
            {
                try
                {
                    var resolved = await _mbResolver
                        .FetchUrlRelsAsync(artist.Mbid, cancellationToken)
                        .ConfigureAwait(false);

                    // Always persist so we know we ran at least once (sentinel prevents re-query).
                    var toStore = new Dictionary<string, string>(resolved)
                    {
                        [MusicBrainzResolver.MbResolvedSentinel] = "true",
                    };

                    await _artistRepository
                        .SetExternalIdsAsync(artist.Id, toStore, cancellationToken)
                        .ConfigureAwait(false);

                    freshExtIds = toStore;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "RefreshConcertsTask: MusicBrainz resolution failed for '{Artist}'.", artist.Name);
                }
            }

            // Use freshly-resolved IDs when available; fall back to the snapshot from GetNextBatchAsync.
            var extIdsForRef = freshExtIds ?? artist.ExternalIds;
            var artistRef = new ArtistRef(artist.Name, artist.Mbid, extIdsForRef);

            bool anySucceeded = false;
            bool anyAttempted = false;
            string? lastError = null;

            foreach (var adapter in adapters)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Check enabled + ToS + configured.
                if (!cfg.EnabledSources.Contains(adapter.Id, StringComparer.OrdinalIgnoreCase))
                    continue;

                if (adapter.RequiresTosOptIn && !IsTosAccepted(adapter.Id, cfg))
                    continue;

                if (!adapter.IsConfigured(cfg))
                    continue;

                // Circuit breaker gate.
                if (await _sourceStateRepository.IsOpenAsync(adapter.Id, now, cancellationToken)
                    .ConfigureAwait(false))
                {
                    _logger.LogDebug(
                        "RefreshConcertsTask: circuit open for source '{Source}' — skipping '{Artist}'.",
                        adapter.Id, artist.Name);
                    continue;
                }

                // Daily budget gate.
                int allowance = await _sourceStateRepository
                    .GetAllowanceAsync(adapter.Id, cfg.PerSourceDailyBudget, cancellationToken)
                    .ConfigureAwait(false);

                if (allowance <= 0)
                {
                    _logger.LogInformation(
                        "RefreshConcertsTask: daily budget exhausted for source '{Source}' — skipping.",
                        adapter.Id);
                    continue;
                }

                anyAttempted = true;

                try
                {
                    int upserts = 0;
                    await foreach (var raw in adapter.FetchAsync(artistRef, sourceFilter, cancellationToken)
                        .ConfigureAwait(false))
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        var record = Normalizer.Normalize(raw, adapter.Id, artistRef, sourceFilter, now);
                        if (record is null)
                            continue;

                        await _concertRepository.UpsertAsync(record, cancellationToken)
                            .ConfigureAwait(false);
                        upserts++;
                    }

                    totalUpserts += upserts;
                    anySucceeded = true;

                    await _sourceStateRepository
                        .RecordSuccessAsync(adapter.Id, now, cancellationToken)
                        .ConfigureAwait(false);

                    _logger.LogInformation(
                        "RefreshConcertsTask: source '{Source}' → '{Artist}': {Upserts} concert(s) upserted.",
                        adapter.Id, artist.Name, upserts);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    totalErrors++;
                    lastError = $"{adapter.Id}: {ex.Message}";

                    _logger.LogWarning(ex,
                        "RefreshConcertsTask: source '{Source}' failed for '{Artist}'.",
                        adapter.Id, artist.Name);

                    await _sourceStateRepository.RecordFailureAsync(
                        adapter.Id,
                        ex.Message,
                        cfg.CircuitBreakerThreshold,
                        TimeSpan.FromHours(cfg.CircuitBreakerCooldownHours),
                        now,
                        cancellationToken).ConfigureAwait(false);
                }
            }

            // Update artist's last_checked_at only if at least one source was attempted and
            // at least one succeeded, or all sources failed (but some were attempted).
            // Per SPEC §9: on all-source failure do NOT advance last_checked_at.
            if (anyAttempted && anySucceeded)
            {
                await _artistRepository
                    .UpdateCheckedAsync(artist.Id, now, lastError, cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (anyAttempted && !anySucceeded)
            {
                // All adapters failed — do not advance last_checked_at; artist retried next run.
                _logger.LogWarning(
                    "RefreshConcertsTask: all sources failed for '{Artist}' — last_checked_at not updated.",
                    artist.Name);
            }
            else if (!anyAttempted)
            {
                // No adapters ran (all disabled/unconfigured/budget) — still advance so we
                // don't stall the round-robin indefinitely.
                await _artistRepository
                    .UpdateCheckedAsync(artist.Id, now, null, cancellationToken)
                    .ConfigureAwait(false);
            }

            processed++;
            progress.Report(batchCount > 0 ? (processed / (double)batchCount) * 100.0 : 100.0);
        }

        // Step 4: garbage collection.
        int pastDeleted = await _concertRepository.DeletePastAsync(now, cancellationToken)
            .ConfigureAwait(false);

        DateTimeOffset staleThreshold = now - TimeSpan.FromDays(cfg.StaleRecordDays);
        int staleDeleted = await _concertRepository.DeleteStaleAsync(staleThreshold, cancellationToken)
            .ConfigureAwait(false);

        sw.Stop();

        _logger.LogInformation(
            "RefreshConcertsTask: completed in {Elapsed}. Artists={Artists}, Upserts={Upserts}, " +
            "Errors={Errors}, PastDeleted={Past}, StaleDeleted={Stale}.",
            sw.Elapsed, processed, totalUpserts, totalErrors, pastDeleted, staleDeleted);

        progress.Report(100);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static Model.SourceFilter BuildSourceFilter(PluginConfiguration cfg, DateTimeOffset now)
    {
        var minDate = now.AddDays(cfg.MinDaysAhead);
        var maxDate = now.AddDays(cfg.MaxDaysAhead);

        return new Model.SourceFilter(
            Locations: cfg.Locations.AsReadOnly(),
            CountryAllowlist: new HashSet<string>(cfg.CountryAllowlist, StringComparer.OrdinalIgnoreCase),
            GenreAllowlist: new HashSet<string>(cfg.GenreAllowlist, StringComparer.OrdinalIgnoreCase),
            MinDate: minDate,
            MaxDate: maxDate,
            SkipFestivals: cfg.SkipFestivals,
            SkipSoldOut: cfg.SkipSoldOut);
    }

    private static bool IsTosAccepted(string sourceId, PluginConfiguration cfg)
        => sourceId switch
        {
            "dice" => cfg.AcceptDiceScrapeTos,
            "ra"   => cfg.AcceptRaScrapeTos,
            _      => false,
        };
}
