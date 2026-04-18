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
/// Unit tests for <see cref="EdmTrainAdapter"/>.
/// Uses <see cref="StubHttpMessageHandler"/>; no live network calls.
/// Placed in <see cref="PluginInstanceCollection"/> to serialize Plugin.Instance mutation.
/// </summary>
[Collection(PluginInstanceCollection.Name)]
public sealed class EdmTrainAdapterTests : IAsyncLifetime
{
    private static readonly ArtistRef Deadmau5WithCachedId = new ArtistRef(
        Name: "deadmau5",
        Mbid: "a0a51b8d-38b0-4c73-a820-5f40e9e1b7c9",
        ExternalIds: new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string> { ["edmtrain"] = "12345" }));

    private static readonly ArtistRef Deadmau5NoExtId = new ArtistRef(
        Name: "deadmau5",
        Mbid: "a0a51b8d-38b0-4c73-a820-5f40e9e1b7c9",
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
            EdmTrainApiKey = "test-edm-api-key",
            RateLimits = new List<SourceRateLimitEntry>
            {
                new SourceRateLimitEntry
                {
                    Source = "edmtrain",
                    Config = new RateLimitConfig { RequestsPerSecond = 1000, RequestsPerDay = null },
                },
            },
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

    private EdmTrainAdapter BuildAdapter(StubHttpMessageHandler handler)
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

        return new EdmTrainAdapter(
            factory,
            rateLimiter,
            _db.Artists,
            _db.SourceState,
            NullLogger<EdmTrainAdapter>.Instance);
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
    /// Happy-path fixture: two events are parsed; mandatory fields are populated.
    /// </summary>
    [Fact]
    public async Task EdmTrainAdapter_ParsesEventFields()
    {
        string eventsBody = FixtureLoader.LoadText("edmtrain/events_deadmau5.json");

        var handler = new StubHttpMessageHandler()
            .OnUrlPattern(@"events\?", HttpStatusCode.OK, eventsBody);

        var adapter = BuildAdapter(handler);
        var events = await CollectAsync(
            adapter.FetchAsync(Deadmau5WithCachedId, OpenFilter, CancellationToken.None));

        events.Should().HaveCount(2);
        var first = events[0];
        first.SourceUrl.Should().Be("https://edmtrain.com/events/99001");
        first.EventDateTime.Should().Be(new DateTimeOffset(2025, 9, 15, 0, 0, 0, TimeSpan.Zero));
        first.ArtistName.Should().Be("deadmau5");
        first.VenueName.Should().Be("Echostage");
        first.City.Should().Be("Washington DC");
        first.Region.Should().Be("DC");
        first.Lat.Should().BeApproximately(38.92, 0.01);
        first.TicketUrl.Should().Be("https://tickets.example.com/99001");
    }

    /// <summary>
    /// Event with festivalInd=true has IsFestival=true.
    /// </summary>
    [Fact]
    public async Task EdmTrainAdapter_HandlesFestivalFlag()
    {
        string eventsBody = FixtureLoader.LoadText("edmtrain/events_deadmau5.json");

        var handler = new StubHttpMessageHandler()
            .OnUrlPattern(@"events\?", HttpStatusCode.OK, eventsBody);

        var adapter = BuildAdapter(handler);
        var events = await CollectAsync(
            adapter.FetchAsync(Deadmau5WithCachedId, OpenFilter, CancellationToken.None));

        events[0].IsFestival.Should().BeFalse("first event has festivalInd=false");
        events[1].IsFestival.Should().BeTrue("second event has festivalInd=true");
    }

    /// <summary>
    /// Empty artists list from the search endpoint yields nothing; no exception.
    /// </summary>
    [Fact]
    public async Task EdmTrainAdapter_HandlesEmptyArtistListResponse_YieldsNothing()
    {
        string artistsEmpty = FixtureLoader.LoadText("edmtrain/artists_empty.json");

        var handler = new StubHttpMessageHandler()
            .OnUrlPattern(@"artists\?", HttpStatusCode.OK, artistsEmpty);

        var adapter = BuildAdapter(handler);
        var events = await CollectAsync(
            adapter.FetchAsync(Deadmau5NoExtId, OpenFilter, CancellationToken.None));

        events.Should().BeEmpty("no EdmTrain artist found means nothing to fetch");
    }

    /// <summary>
    /// When the attraction ID is already cached in ext_ids, the artist lookup endpoint is NOT
    /// called; only the events endpoint is called.
    /// Then a second FetchAsync with the same cached ref also skips the lookup. Verifies
    /// the cache-hit path by confirming no calls to /artists?.
    /// </summary>
    [Fact]
    public async Task EdmTrainAdapter_ResolvesArtistIdOnce_Cached()
    {
        // Insert artist row so SetExternalIdsAsync can store the persisted ID.
        await _db.Artists.UpsertFromLibraryAsync(
            new[]
            {
                new LibraryArtist(
                    Mbid: Deadmau5NoExtId.Mbid,
                    Name: Deadmau5NoExtId.Name,
                    JellyfinItemId: Guid.NewGuid()),
            },
            CancellationToken.None);

        string artistsBody = FixtureLoader.LoadText("edmtrain/artists_deadmau5.json");
        string eventsBody  = FixtureLoader.LoadText("edmtrain/events_deadmau5.json");

        int artistLookupCount = 0;

        var handler = new StubHttpMessageHandler()
            .OnUrlPattern(@"/artists\?", (_, _) =>
            {
                artistLookupCount++;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        artistsBody, System.Text.Encoding.UTF8, "application/json"),
                };
            })
            .OnUrlPattern(@"/events\?", HttpStatusCode.OK, eventsBody);

        var adapter = BuildAdapter(handler);

        // First fetch: no cached ID → must look up artist.
        await CollectAsync(adapter.FetchAsync(Deadmau5NoExtId, OpenFilter, CancellationToken.None));
        artistLookupCount.Should().Be(1, "first fetch must resolve artist ID");

        // Verify ID was persisted in DB.
        var stored = await _db.Artists.GetNextBatchAsync(10, CancellationToken.None);
        stored.Should().ContainSingle()
            .Which.ExternalIds.Should().ContainKey("edmtrain");

        // Second fetch with cached ext_ids (simulate what the scheduler would do after persisting).
        var cachedRef = new ArtistRef(
            Name: Deadmau5NoExtId.Name,
            Mbid: Deadmau5NoExtId.Mbid,
            ExternalIds: new ReadOnlyDictionary<string, string>(
                new Dictionary<string, string> { ["edmtrain"] = "12345" }));

        await CollectAsync(adapter.FetchAsync(cachedRef, OpenFilter, CancellationToken.None));
        artistLookupCount.Should().Be(1,
            "second fetch with cached ID must NOT call the artist lookup endpoint again");
    }

    /// <summary>
    /// When the server returns 500 twice then 200, the adapter succeeds (retry logic).
    /// Retry-After: 0 keeps the test fast.
    /// </summary>
    [Fact]
    public async Task EdmTrainAdapter_Retries_On5xx()
    {
        string eventsBody = FixtureLoader.LoadText("edmtrain/events_deadmau5.json");
        int callCount = 0;

        var handler = new StubHttpMessageHandler()
            .OnUrlPattern(@"events\?", (_, _) =>
            {
                callCount++;
                if (callCount <= 2)
                {
                    var resp500 = new HttpResponseMessage(HttpStatusCode.InternalServerError)
                    {
                        Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
                    };
                    resp500.Headers.RetryAfter =
                        new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
                    return resp500;
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        eventsBody, System.Text.Encoding.UTF8, "application/json"),
                };
            });

        var adapter = BuildAdapter(handler);
        var events = await CollectAsync(
            adapter.FetchAsync(Deadmau5WithCachedId, OpenFilter, CancellationToken.None));

        callCount.Should().Be(3, "two 500s then one 200 = 3 total attempts");
        events.Should().HaveCount(2);
    }
}
