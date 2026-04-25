using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Jellyfin.Plugin.ConcertRadar.Configuration;
using Jellyfin.Plugin.ConcertRadar.Library;
using Jellyfin.Plugin.ConcertRadar.Model;
using Jellyfin.Plugin.ConcertRadar.RateLimiting;
using Jellyfin.Plugin.ConcertRadar.Resolution;
using Jellyfin.Plugin.ConcertRadar.ScheduledTasks;
using Jellyfin.Plugin.ConcertRadar.Sources;
using Jellyfin.Plugin.ConcertRadar.Tests.Support;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.ConcertRadar.Tests.ScheduledTasks;

/// <summary>
/// Integration tests for <see cref="RefreshConcertsTask"/>.
/// Uses real SQLite (via <see cref="TestDatabase"/>), <see cref="FakeSourceAdapter"/>,
/// and <see cref="TimeProviderStub"/> for deterministic time control.
/// Placed in <see cref="Sources.PluginInstanceCollection"/> to serialize mutations of the
/// static <c>Plugin.Instance</c> field (RefreshConcertsTask reads from it at runtime).
/// </summary>
[Collection(Sources.PluginInstanceCollection.Name)]
public sealed class RefreshConcertsTaskTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now =
        new DateTimeOffset(2025, 6, 15, 12, 0, 0, TimeSpan.Zero);

    private TestDatabase _db = null!;
    private TimeProviderStub _clock = null!;
    private PluginConfiguration _cfg = null!;

    public async Task InitializeAsync()
    {
        _clock = new TimeProviderStub(Now);
        _db = await TestDatabase.CreateAsync(_clock);

        _cfg = new PluginConfiguration
        {
            TicketmasterApiKey = "test-key",
            EdmTrainApiKey = "test-edm-key",
            MaxArtistsPerRun = 50,
            PerSourceDailyBudget = 500,
            StaleRecordDays = 14,
            CircuitBreakerThreshold = 5,
            CircuitBreakerCooldownHours = 24,
            EnabledSources = new List<string> { "fake1", "fake2" },
            Locations = new List<LocationFilter>(),
            CountryAllowlist = new List<string>(),
            GenreAllowlist = new List<string>(),
            MinDaysAhead = 0,
            MaxDaysAhead = 365,
        };

        SetupPluginInstance(_cfg);
    }

    public async Task DisposeAsync()
        => await _db.DisposeAsync();

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void SetupPluginInstance(PluginConfiguration cfg)
    {
        var paths = Substitute.For<IApplicationPaths>();
        paths.DataPath.Returns(System.IO.Path.GetTempPath());
        var serializer = Substitute.For<IXmlSerializer>();
        serializer.DeserializeFromFile(Arg.Any<Type>(), Arg.Any<string>()).Returns(cfg);
        _ = new Plugin(paths, serializer);
    }

    private static MusicArtist MakeMusicArtist(string name, string? mbid = null)
    {
        var artist = new MusicArtist
        {
            Id   = Guid.NewGuid(),
            Name = name,
        };
        if (mbid is not null)
            artist.ProviderIds["MusicBrainzArtist"] = mbid;
        return artist;
    }

    /// <summary>
    /// Builds a <see cref="RefreshConcertsTask"/> wired to the real test DB.
    /// The <see cref="LibraryArtistEnumerator"/> returns the given library artists.
    /// MusicBrainzResolver uses a stub HTTP handler that returns 404 (no-op).
    /// </summary>
    private RefreshConcertsTask BuildTask(
        IEnumerable<ISourceAdapter> adapters,
        IEnumerable<MusicArtist> libraryArtists)
    {
        var libraryManager = Substitute.For<ILibraryManager>();
        libraryManager.GetItemList(Arg.Any<InternalItemsQuery>())
                      .Returns(libraryArtists.ToList());

        var enumerator = new LibraryArtistEnumerator(
            libraryManager,
            NullLogger<LibraryArtistEnumerator>.Instance);

        // MusicBrainzResolver that always gets 404 — effectively no-op (skips resolution).
        var mbHandler = new StubHttpMessageHandler()
            .AlwaysReturn(HttpStatusCode.NotFound, "{}");
        var mbFactory = Substitute.For<IHttpClientFactory>();
        mbFactory.CreateClient(Arg.Any<string>()).Returns(_ =>
            new HttpClient(mbHandler) { BaseAddress = null });

        var mbConfigProvider = new StubPluginConfigurationProvider(_cfg);
        var mbRateLimiter = new HostRateLimiter(
            _db.SourceState,
            mbConfigProvider,
            _clock,
            NullLogger<HostRateLimiter>.Instance);

        var mbResolver = new MusicBrainzResolver(
            mbFactory,
            mbRateLimiter,
            mbConfigProvider,
            NullLogger<MusicBrainzResolver>.Instance);

        return new RefreshConcertsTask(
            enumerator,
            _db.Artists,
            _db.Concerts,
            _db.SourceState,
            mbResolver,
            adapters,
            _clock,
            NullLogger<RefreshConcertsTask>.Instance);
    }

    private static RawEvent MakeRawEvent(string sourceEventId, string artistName)
        => new RawEvent(
            SourceEventId: sourceEventId,
            SourceUrl: $"https://example.com/event/{sourceEventId}",
            ArtistName: artistName,
            EventDateTime: Now.AddDays(30),
            VenueName: "Test Venue",
            VenueAddress: null,
            City: "Test City",
            Region: null,
            Country: "US",
            Lat: null,
            Lon: null,
            Lineup: new[] { artistName },
            TicketUrl: null,
            PriceMin: null,
            PriceMax: null,
            Currency: null,
            OnSaleAt: null,
            IsFestival: false,
            IsSoldOut: false);

    private static IProgress<double> CaptureProgress(List<double> captures)
    {
        var progress = Substitute.For<IProgress<double>>();
        progress.When(p => p.Report(Arg.Any<double>()))
                .Do(ci => captures.Add(ci.Arg<double>()));
        return progress;
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Two adapters, 1 artist → concerts from both sources are upserted, keyed by (source, source_event_id).
    /// </summary>
    [Fact]
    public async Task Task_UpsertsConcertsAcrossEnabledSources()
    {
        var adapter1 = new FakeSourceAdapter("fake1",
            events: new[] { MakeRawEvent("evt-a1", "Artist1") });
        var adapter2 = new FakeSourceAdapter("fake2",
            events: new[] { MakeRawEvent("evt-a2", "Artist1") });

        var task = BuildTask(
            new ISourceAdapter[] { adapter1, adapter2 },
            new[] { MakeMusicArtist("Artist1") });

        await task.ExecuteAsync(Substitute.For<IProgress<double>>(), CancellationToken.None);

        var result = await _db.Concerts.QueryAsync(
            new ConcertQuery(null, null, null, null, null, null, ConcertSortField.Date, 0, 50),
            CancellationToken.None);

        result.Total.Should().Be(2, "one concert per source per artist");
        result.Items.Should().Contain(c => c.Source == "fake1" && c.SourceEventId == "evt-a1");
        result.Items.Should().Contain(c => c.Source == "fake2" && c.SourceEventId == "evt-a2");
    }

    /// <summary>
    /// An adapter not in EnabledSources is never called.
    /// </summary>
    [Fact]
    public async Task Task_SkipsDisabledSources()
    {
        _cfg.EnabledSources = new List<string> { "fake1" }; // "fake2" not included
        SetupPluginInstance(_cfg);

        var adapter1 = new FakeSourceAdapter("fake1",
            events: new[] { MakeRawEvent("evt-1", "Artist1") });
        var adapter2 = new FakeSourceAdapter("fake2",
            events: new[] { MakeRawEvent("evt-2", "Artist1") });

        var task = BuildTask(
            new ISourceAdapter[] { adapter1, adapter2 },
            new[] { MakeMusicArtist("Artist1") });

        await task.ExecuteAsync(Substitute.For<IProgress<double>>(), CancellationToken.None);

        adapter1.CallCount.Should().Be(1, "fake1 is enabled");
        adapter2.CallCount.Should().Be(0, "fake2 is disabled");
    }

    /// <summary>
    /// An adapter whose source_state has disabled_until > now (open circuit) is skipped.
    /// </summary>
    [Fact]
    public async Task Task_SkipsSourcesWithOpenCircuit()
    {
        _cfg.EnabledSources = new List<string> { "fake1" };
        SetupPluginInstance(_cfg);

        // Pre-open the circuit for fake1: threshold=1 so one failure opens it.
        await _db.SourceState.RecordFailureAsync(
            "fake1",
            "simulated failure",
            threshold: 1,
            cooldown: TimeSpan.FromHours(24),
            now: Now,
            CancellationToken.None);

        var adapter1 = new FakeSourceAdapter("fake1",
            events: new[] { MakeRawEvent("evt-open", "Artist1") });

        var task = BuildTask(
            new ISourceAdapter[] { adapter1 },
            new[] { MakeMusicArtist("Artist1") });

        await task.ExecuteAsync(Substitute.For<IProgress<double>>(), CancellationToken.None);

        adapter1.CallCount.Should().Be(0,
            "circuit is open for fake1 → adapter must not be called");
    }

    /// <summary>
    /// When all sources fail for an artist, last_checked_at is NOT advanced (SPEC §9).
    /// </summary>
    [Fact]
    public async Task Task_DoesNotAdvanceLastCheckedAt_WhenAllSourcesFail()
    {
        _cfg.EnabledSources = new List<string> { "fake1" };
        SetupPluginInstance(_cfg);

        var throwingAdapter = new FakeSourceAdapter(
            "fake1",
            new InvalidOperationException("simulated failure"));

        var task = BuildTask(
            new ISourceAdapter[] { throwingAdapter },
            new[] { MakeMusicArtist("Artist1", "mbid-001") });

        await task.ExecuteAsync(Substitute.For<IProgress<double>>(), CancellationToken.None);

        var stored = await _db.Artists.GetNextBatchAsync(10, CancellationToken.None);
        stored.Should().ContainSingle()
            .Which.LastCheckedAt.Should().BeNull(
                "all sources failed → last_checked_at must not be updated per SPEC §9");
    }

    /// <summary>
    /// When at least one source succeeds, last_checked_at IS advanced even if others fail.
    /// </summary>
    [Fact]
    public async Task Task_AdvancesLastCheckedAt_OnPartialFailure()
    {
        _cfg.EnabledSources = new List<string> { "fake1", "fake2" };
        SetupPluginInstance(_cfg);

        var goodAdapter = new FakeSourceAdapter("fake1",
            events: new[] { MakeRawEvent("evt-ok", "Artist1") });
        var throwingAdapter = new FakeSourceAdapter(
            "fake2",
            new InvalidOperationException("simulated partial failure"));

        var task = BuildTask(
            new ISourceAdapter[] { goodAdapter, throwingAdapter },
            new[] { MakeMusicArtist("Artist1", "mbid-001") });

        await task.ExecuteAsync(Substitute.For<IProgress<double>>(), CancellationToken.None);

        var stored = await _db.Artists.GetNextBatchAsync(10, CancellationToken.None);
        stored.Should().ContainSingle()
            .Which.LastCheckedAt.Should().NotBeNull(
                "partial success → last_checked_at must be advanced");
    }

    /// <summary>
    /// A past-dated event inserted before the run is deleted by GC.
    /// </summary>
    [Fact]
    public async Task Task_GcRemovesPastEvents()
    {
        // Insert a past event directly.
        var pastRecord = ConcertRecordFactory.Create(
            source: "fake1",
            sourceEventId: "past-evt",
            eventDateTime: Now.AddDays(-10)); // 10 days in the past

        await _db.Concerts.UpsertAsync(pastRecord, CancellationToken.None);

        var task = BuildTask(
            new ISourceAdapter[] { new FakeSourceAdapter("fake1") },
            new[] { MakeMusicArtist("Artist1") });

        await task.ExecuteAsync(Substitute.For<IProgress<double>>(), CancellationToken.None);

        var result = await _db.Concerts.QueryAsync(
            new ConcertQuery(null, null, null, null, null, null, ConcertSortField.Date, 0, 50),
            CancellationToken.None);

        result.Items.Should().NotContain(
            c => c.SourceEventId == "past-evt",
            "past events must be deleted by GC");
    }

    /// <summary>
    /// A record with last_seen_at older than StaleRecordDays is deleted by GC.
    /// </summary>
    [Fact]
    public async Task Task_GcRemovesStaleEvents()
    {
        _cfg.StaleRecordDays = 14;
        SetupPluginInstance(_cfg);

        // last_seen_at is 20 days ago (beyond 14-day threshold), but event is in future.
        var staleRecord = ConcertRecordFactory.Create(
            source: "fake1",
            sourceEventId: "stale-evt",
            eventDateTime: Now.AddDays(30),
            lastSeenAt: Now.AddDays(-20));

        await _db.Concerts.UpsertAsync(staleRecord, CancellationToken.None);

        var task = BuildTask(
            new ISourceAdapter[] { new FakeSourceAdapter("fake1") },
            new[] { MakeMusicArtist("Artist1") });

        await task.ExecuteAsync(Substitute.For<IProgress<double>>(), CancellationToken.None);

        var result = await _db.Concerts.QueryAsync(
            new ConcertQuery(null, null, null, null, null, null, ConcertSortField.Date, 0, 50),
            CancellationToken.None);

        result.Items.Should().NotContain(
            c => c.SourceEventId == "stale-evt",
            "events not seen in StaleRecordDays days must be deleted by GC");
    }

    /// <summary>
    /// PerSourceDailyBudget=2 with 3 artists: only 2 invocations for the source.
    /// The FakeSourceAdapter is wired with the real SourceStateRepository so that
    /// each call increments calls_today, exactly as real adapters do via HostRateLimiter.
    /// </summary>
    [Fact]
    public async Task Task_RespectsPerSourceDailyBudget()
    {
        _cfg.EnabledSources = new List<string> { "fake1" };
        _cfg.PerSourceDailyBudget = 2;
        _cfg.MaxArtistsPerRun = 10;
        SetupPluginInstance(_cfg);

        // Pass the real SourceStateRepository so FetchAsync increments calls_today.
        var adapter = new FakeSourceAdapter(
            id: "fake1",
            events: new[] { MakeRawEvent("evt-budget", "ArtistX") },
            sourceState: _db.SourceState);

        var task = BuildTask(
            new ISourceAdapter[] { adapter },
            new[]
            {
                MakeMusicArtist("Artist1"),
                MakeMusicArtist("Artist2"),
                MakeMusicArtist("Artist3"),
            });

        await task.ExecuteAsync(Substitute.For<IProgress<double>>(), CancellationToken.None);

        adapter.CallCount.Should().Be(2,
            "PerSourceDailyBudget=2 stops the source after 2 artist invocations");
    }

    /// <summary>
    /// Progress is reported starting at 0 and ending at 100.
    /// </summary>
    [Fact]
    public async Task Task_ReportsProgress_FromZeroToHundred()
    {
        var captured = new List<double>();
        var progress = CaptureProgress(captured);

        var adapter = new FakeSourceAdapter("fake1",
            events: new[] { MakeRawEvent("evt-p", "Artist1") });

        var task = BuildTask(
            new ISourceAdapter[] { adapter },
            new[] { MakeMusicArtist("Artist1") });

        await task.ExecuteAsync(progress, CancellationToken.None);

        captured.Should().NotBeEmpty("progress must be reported at least once");
        captured.First().Should().Be(0.0, "first progress report must be 0");
        captured.Last().Should().Be(100.0, "last progress report must be 100");
    }

    /// <summary>
    /// Cancellation before or during the run propagates as OperationCanceledException.
    /// </summary>
    [Fact]
    public async Task Task_IsCancellable()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync(); // cancel immediately

        var adapter = new FakeSourceAdapter("fake1",
            events: new[] { MakeRawEvent("evt-c", "Artist1") });

        var task = BuildTask(
            new ISourceAdapter[] { adapter },
            new[] { MakeMusicArtist("Artist1"), MakeMusicArtist("Artist2") });

        var act = () => task.ExecuteAsync(Substitute.For<IProgress<double>>(), cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
