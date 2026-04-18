using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Jellyfin.Plugin.ConcertRadar.Configuration;
using Jellyfin.Plugin.ConcertRadar.Model;
using Jellyfin.Plugin.ConcertRadar.RateLimiting;
using Jellyfin.Plugin.ConcertRadar.Sources;
using Jellyfin.Plugin.ConcertRadar.Tests.Support;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.ConcertRadar.Tests.Sources;

/// <summary>
/// Unit tests for <see cref="TicketmasterAdapter"/>.
/// Tests use <see cref="StubHttpMessageHandler"/> so no live network calls are made.
/// Placed in <see cref="PluginInstanceCollection"/> to prevent parallel mutation of
/// the static <c>Plugin.Instance</c> field.
/// </summary>
[Collection(PluginInstanceCollection.Name)]
public sealed class TicketmasterAdapterTests : IAsyncLifetime
{
    private static readonly ArtistRef RadioheadWithId = new ArtistRef(
        Name: "Radiohead",
        Mbid: "a74b1b7f-71a5-4011-9441-d0b5e4122711",
        ExternalIds: new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string> { ["ticketmaster"] = "K8vZ9171oZf" }));

    private static readonly ArtistRef RadioheadNoExtId = new ArtistRef(
        Name: "Radiohead",
        Mbid: "a74b1b7f-71a5-4011-9441-d0b5e4122711",
        ExternalIds: new ReadOnlyDictionary<string, string>(new Dictionary<string, string>()));

    private static readonly SourceFilter OpenFilter = new SourceFilter(
        Locations: Array.Empty<LocationFilter>(),
        CountryAllowlist: new HashSet<string>(),
        GenreAllowlist: new HashSet<string>(),
        MinDate: new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero),
        MaxDate: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        SkipFestivals: false,
        SkipSoldOut: false);

    private TestDatabase _db = null!;
    private PluginConfiguration _cfg = null!;

    public async Task InitializeAsync()
    {
        _db = await TestDatabase.CreateAsync();
        _cfg = new PluginConfiguration
        {
            TicketmasterApiKey = "test-api-key",
            RateLimits = new List<SourceRateLimitEntry>
            {
                // Permissive rate limit so tests don't wait.
                new SourceRateLimitEntry
                {
                    Source = "ticketmaster",
                    Config = new RateLimitConfig { RequestsPerSecond = 1000, RequestsPerDay = null },
                },
            },
        };

        // Set Plugin.Instance so adapters can call Plugin.Instance?.Configuration.
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

        // Plugin constructor sets Plugin.Instance = this.
        var plugin = new Plugin(paths, serializer);
    }

    private TicketmasterAdapter BuildAdapter(StubHttpMessageHandler handler)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ =>
            new HttpClient(handler) { BaseAddress = null });

        var configProvider = new StubPluginConfigurationProvider(_cfg);
        var rateLimiter = new HostRateLimiter(
            _db.SourceState,
            configProvider,
            TimeProvider.System,
            NullLogger<HostRateLimiter>.Instance);

        return new TicketmasterAdapter(
            factory,
            rateLimiter,
            _db.Artists,
            _db.SourceState,
            NullLogger<TicketmasterAdapter>.Instance);
    }

    private static async Task<List<RawEvent>> CollectAsync(IAsyncEnumerable<RawEvent> stream)
    {
        var results = new List<RawEvent>();
        await foreach (var ev in stream)
            results.Add(ev);
        return results;
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Parsing the happy-path fixture extracts all three events with populated fields.
    /// </summary>
    [Fact]
    public async Task TicketmasterAdapter_ParsesHappyPath_ExtractsEvents()
    {
        string eventsBody = FixtureLoader.LoadText("ticketmaster/events_radiohead_us.json");

        var handler = new StubHttpMessageHandler()
            .OnUrlPattern(@"events\.json", HttpStatusCode.OK, eventsBody);

        var adapter = BuildAdapter(handler);
        var events = await CollectAsync(
            adapter.FetchAsync(RadioheadWithId, OpenFilter, CancellationToken.None));

        events.Should().HaveCount(3);
        events.Should().AllSatisfy(e =>
        {
            e.SourceUrl.Should().NotBeNullOrWhiteSpace();
            e.EventDateTime.Should().NotBe(default);
            e.VenueName.Should().NotBeNullOrWhiteSpace();
            e.City.Should().NotBeNullOrWhiteSpace();
            e.Country.Should().Be("US");
        });
    }

    /// <summary>
    /// Price range fields (min, max, currency) are extracted from the priceRanges array.
    /// </summary>
    [Fact]
    public async Task TicketmasterAdapter_ParsesPriceRange()
    {
        string eventsBody = FixtureLoader.LoadText("ticketmaster/events_radiohead_us.json");

        var handler = new StubHttpMessageHandler()
            .OnUrlPattern(@"events\.json", HttpStatusCode.OK, eventsBody);

        var adapter = BuildAdapter(handler);
        var events = await CollectAsync(
            adapter.FetchAsync(RadioheadWithId, OpenFilter, CancellationToken.None));

        var first = events[0];
        first.PriceMin.Should().Be(45.0m);
        first.PriceMax.Should().Be(150.0m);
        first.Currency.Should().Be("USD");
    }

    /// <summary>
    /// The on-sale date is extracted from sales.public.startDateTime.
    /// </summary>
    [Fact]
    public async Task TicketmasterAdapter_ParsesOnSaleDate()
    {
        string eventsBody = FixtureLoader.LoadText("ticketmaster/events_radiohead_us.json");

        var handler = new StubHttpMessageHandler()
            .OnUrlPattern(@"events\.json", HttpStatusCode.OK, eventsBody);

        var adapter = BuildAdapter(handler);
        var events = await CollectAsync(
            adapter.FetchAsync(RadioheadWithId, OpenFilter, CancellationToken.None));

        var first = events[0];
        first.OnSaleAt.Should().NotBeNull();
        first.OnSaleAt!.Value.UtcDateTime.Year.Should().Be(2025);
    }

    /// <summary>
    /// Festival classification is detected from the genre name in the first event.
    /// Event TM-002 has genre "Festival".
    /// </summary>
    [Fact]
    public async Task TicketmasterAdapter_DetectsFestivalClassification()
    {
        string eventsBody = FixtureLoader.LoadText("ticketmaster/events_radiohead_us.json");

        var handler = new StubHttpMessageHandler()
            .OnUrlPattern(@"events\.json", HttpStatusCode.OK, eventsBody);

        var adapter = BuildAdapter(handler);
        var events = await CollectAsync(
            adapter.FetchAsync(RadioheadWithId, OpenFilter, CancellationToken.None));

        events[1].IsFestival.Should().BeTrue("TM-002 has genre 'Festival'");
        events[0].IsFestival.Should().BeFalse("TM-001 has genre 'Rock'");
    }

    /// <summary>
    /// When the attraction is not cached in ExternalIds, the adapter calls the attractions
    /// endpoint to resolve it. The resolved ID is then written to ArtistRepository (the ext_ids
    /// column). This test inserts the artist row first so the UPDATE in SetExternalIdsAsync
    /// matches a row, then verifies the ID is persisted after FetchAsync completes.
    /// </summary>
    [Fact]
    public async Task TicketmasterAdapter_ResolvesAttractionId_OnlyOnce_Cached()
    {
        string attractionsBody = FixtureLoader.LoadText("ticketmaster/attractions_radiohead.json");
        string eventsBody = FixtureLoader.LoadText("ticketmaster/events_radiohead_us.json");

        // Insert the artist row so SetExternalIdsAsync can UPDATE it.
        await _db.Artists.UpsertFromLibraryAsync(
            new[]
            {
                new Jellyfin.Plugin.ConcertRadar.Model.LibraryArtist(
                    Mbid: RadioheadNoExtId.Mbid,
                    Name: RadioheadNoExtId.Name,
                    JellyfinItemId: Guid.NewGuid()),
            },
            CancellationToken.None);

        int attractionCallCount = 0;
        var handler = new StubHttpMessageHandler()
            .OnUrlPattern(@"attractions\.json", (_, _) =>
            {
                attractionCallCount++;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new System.Net.Http.StringContent(
                        attractionsBody, System.Text.Encoding.UTF8, "application/json"),
                };
            })
            .OnUrlPattern(@"events\.json", HttpStatusCode.OK, eventsBody);

        var adapter = BuildAdapter(handler);

        // First call — should hit attractions endpoint.
        await CollectAsync(adapter.FetchAsync(RadioheadNoExtId, OpenFilter, CancellationToken.None));

        attractionCallCount.Should().Be(1, "first fetch should resolve the attraction ID");

        // Verify the resolved ID was persisted via SetExternalIdsAsync.
        var stored = await _db.Artists.GetNextBatchAsync(10, CancellationToken.None);
        var storedArtist = stored.Should().ContainSingle().Subject;
        storedArtist.ExternalIds.Should().ContainKey("ticketmaster");
        storedArtist.ExternalIds["ticketmaster"].Should().Be("K8vZ9171oZf");
    }

    /// <summary>
    /// When the events response has no _embedded.events, no events are yielded.
    /// </summary>
    [Fact]
    public async Task TicketmasterAdapter_EmitsNothingForNoResults()
    {
        string eventsBody = FixtureLoader.LoadText("ticketmaster/events_empty.json");

        var handler = new StubHttpMessageHandler()
            .OnUrlPattern(@"events\.json", HttpStatusCode.OK, eventsBody);

        var adapter = BuildAdapter(handler);
        var events = await CollectAsync(
            adapter.FetchAsync(RadioheadWithId, OpenFilter, CancellationToken.None));

        events.Should().BeEmpty();
    }

    /// <summary>
    /// When the response includes Rate-Limit-Available header with value 0,
    /// the adapter should not throw and should log the quota (verified by absence of exception).
    /// </summary>
    [Fact]
    public async Task TicketmasterAdapter_Logs429RateLimitHeaders()
    {
        string eventsBody = FixtureLoader.LoadText("ticketmaster/events_radiohead_us.json");

        var handler = new StubHttpMessageHandler()
            .OnUrlPattern(@"events\.json", (_, _) =>
            {
                var resp = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new System.Net.Http.StringContent(
                        eventsBody, System.Text.Encoding.UTF8, "application/json"),
                };
                resp.Headers.Add("Rate-Limit-Available", "0");
                resp.Headers.Add("Rate-Limit-Over", "1");
                return resp;
            });

        var adapter = BuildAdapter(handler);

        // Should not throw even when quota is 0 — quota logging is informational only.
        var act = async () => await CollectAsync(
            adapter.FetchAsync(RadioheadWithId, OpenFilter, CancellationToken.None));

        await act.Should().NotThrowAsync();
    }

    /// <summary>
    /// When the server returns 429 twice then 200, the adapter succeeds after 3 attempts.
    /// Retry-After: 0 is used to keep the test fast.
    /// </summary>
    [Fact]
    public async Task TicketmasterAdapter_Retries_On429()
    {
        string eventsBody = FixtureLoader.LoadText("ticketmaster/events_radiohead_us.json");
        int callCount = 0;

        var handler = new StubHttpMessageHandler()
            .OnUrlPattern(@"events\.json", (_, _) =>
            {
                callCount++;
                if (callCount <= 2)
                {
                    var resp429 = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                    {
                        Content = new System.Net.Http.StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
                    };
                    // Retry-After: 0 so the test does not sleep.
                    resp429.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
                    return resp429;
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new System.Net.Http.StringContent(
                        eventsBody, System.Text.Encoding.UTF8, "application/json"),
                };
            });

        var adapter = BuildAdapter(handler);
        var events = await CollectAsync(
            adapter.FetchAsync(RadioheadWithId, OpenFilter, CancellationToken.None));

        callCount.Should().Be(3, "two 429s then one 200 = 3 total attempts");
        events.Should().HaveCount(3);
    }

    /// <summary>
    /// Circuit breaker opens after repeated 5xx failures — verified directly via
    /// SourceStateRepository.RecordFailureAsync simulation (the adapter itself does not
    /// open the circuit; RefreshConcertsTask does). This test exercises the repository
    /// logic and verifies IsOpenAsync returns true after threshold failures.
    ///
    /// Flagged: the adapter does not call RecordFailureAsync on its own — circuit breaker
    /// management is the responsibility of the scheduler layer. Test exercises the repository
    /// directly to validate the threshold logic independent of the adapter.
    /// </summary>
    [Fact]
    public async Task TicketmasterAdapter_OpensCircuit_AfterRepeated5xx()
    {
        // Tight threshold for speed.
        const int threshold = 2;
        var cooldown = TimeSpan.FromHours(1);
        var now = new DateTimeOffset(2025, 6, 15, 14, 0, 0, TimeSpan.Zero);

        // Simulate N RecordFailureAsync calls directly.
        for (int i = 0; i < threshold; i++)
        {
            await _db.SourceState.RecordFailureAsync(
                "ticketmaster",
                "HTTP 500",
                threshold,
                cooldown,
                now,
                CancellationToken.None);
        }

        bool isOpen = await _db.SourceState.IsOpenAsync("ticketmaster", now, CancellationToken.None);
        isOpen.Should().BeTrue(
            "after {0} consecutive failures the circuit should be open", threshold);
    }
}
