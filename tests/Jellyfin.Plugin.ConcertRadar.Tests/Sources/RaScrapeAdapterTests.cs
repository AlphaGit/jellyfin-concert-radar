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
/// Contract (fixture-backed) tests for <see cref="RaScrapeAdapter"/>.
/// Validates that the GraphQL response shapes are parsed correctly.
/// These tests are drift detectors — if RA changes its GraphQL schema, these break first.
/// </summary>
[Collection(PluginInstanceCollection.Name)]
public sealed class RaScrapeAdapterTests : IAsyncLifetime
{
    private static readonly ArtistRef Deadmau5WithSlug = new ArtistRef(
        Name: "deadmau5",
        Mbid: null,
        ExternalIds: new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string> { ["ra"] = "deadmau5" }));

    private static readonly ArtistRef Deadmau5NoExtId = new ArtistRef(
        Name: "deadmau5",
        Mbid: null,
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
            AcceptRaScrapeTos = true,
            EnabledSources = new List<string> { "ra" },
            RateLimits = new List<SourceRateLimitEntry>
            {
                new SourceRateLimitEntry
                {
                    Source = "ra",
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

    private RaScrapeAdapter BuildAdapter(StubHttpMessageHandler handler)
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

        return new RaScrapeAdapter(
            factory,
            rateLimiter,
            _db.Artists,
            _db.SourceState,
            NullLogger<RaScrapeAdapter>.Instance);
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
    /// Happy-path fixture: parser does not throw and produces ≥1 RawEvent.
    /// </summary>
    [Fact]
    public async Task RaAdapter_HappyPathFixture_ProducesEvents()
    {
        string eventsJson = FixtureLoader.LoadText("ra/eventlistings_happy.json");

        var handler = new StubHttpMessageHandler()
            .OnUrlPattern(@"ra\.co/graphql", HttpStatusCode.OK, eventsJson);

        var adapter = BuildAdapter(handler);

        var act = async () => await CollectAsync(
            adapter.FetchAsync(Deadmau5WithSlug, OpenFilter, CancellationToken.None));

        var events = await act.Should().NotThrowAsync();
        events.Subject.Should().HaveCountGreaterThanOrEqualTo(1,
            "happy-path fixture contains event listings data");
    }

    /// <summary>
    /// Mandatory fields (SourceUrl, EventDateTime, ArtistName) are populated for every event.
    /// </summary>
    [Fact]
    public async Task RaAdapter_MandatoryFieldsPopulated()
    {
        string eventsJson = FixtureLoader.LoadText("ra/eventlistings_happy.json");

        var handler = new StubHttpMessageHandler()
            .OnUrlPattern(@"ra\.co/graphql", HttpStatusCode.OK, eventsJson);

        var adapter = BuildAdapter(handler);
        var events = await CollectAsync(
            adapter.FetchAsync(Deadmau5WithSlug, OpenFilter, CancellationToken.None));

        events.Should().AllSatisfy(e =>
        {
            e.SourceUrl.Should().NotBeNullOrWhiteSpace("SourceUrl is mandatory");
            e.EventDateTime.Should().NotBe(default, "EventDateTime is mandatory");
            e.ArtistName.Should().NotBeNullOrWhiteSpace("ArtistName is mandatory");
        });
    }

    /// <summary>
    /// The GraphQL response shape (data.eventListings.data[].event) is correctly traversed.
    /// </summary>
    [Fact]
    public async Task RaAdapter_ParsesEventListingsResponse()
    {
        string eventsJson = FixtureLoader.LoadText("ra/eventlistings_happy.json");

        var handler = new StubHttpMessageHandler()
            .OnUrlPattern(@"ra\.co/graphql", HttpStatusCode.OK, eventsJson);

        var adapter = BuildAdapter(handler);
        var events = await CollectAsync(
            adapter.FetchAsync(Deadmau5WithSlug, OpenFilter, CancellationToken.None));

        events.Should().HaveCount(2, "fixture has two events");

        var first = events[0];
        first.SourceUrl.Should().Contain("ra.co/events/100001");
        first.VenueName.Should().Be("fabric");
        first.City.Should().Be("London");
        first.Country.Should().Be("United Kingdom");
        first.Lineup.Should().ContainInOrder("deadmau5", "Four Tet");
    }

    /// <summary>
    /// When no cached slug exists, the adapter uses the artist search query to resolve it.
    /// </summary>
    [Fact]
    public async Task RaAdapter_ResolvesArtistSlugFromSearch()
    {
        string searchJson = FixtureLoader.LoadText("ra/artistsearch_happy.json");
        string eventsJson = FixtureLoader.LoadText("ra/eventlistings_happy.json");

        int graphqlCalls = 0;
        var handler = new StubHttpMessageHandler()
            .OnUrlPattern(@"ra\.co/graphql", (req, _) =>
            {
                graphqlCalls++;
                // First call is the artist search; subsequent calls are event listings.
                string body = graphqlCalls == 1 ? searchJson : eventsJson;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
                };
            });

        // Insert artist row for SetExternalIdsAsync.
        await _db.Artists.UpsertFromLibraryAsync(
            new[]
            {
                new LibraryArtist(
                    Mbid: Deadmau5NoExtId.Mbid,
                    Name: Deadmau5NoExtId.Name,
                    JellyfinItemId: Guid.NewGuid()),
            },
            CancellationToken.None);

        var adapter = BuildAdapter(handler);
        var events = await CollectAsync(
            adapter.FetchAsync(Deadmau5NoExtId, OpenFilter, CancellationToken.None));

        graphqlCalls.Should().BeGreaterThanOrEqualTo(2,
            "first call resolves slug, second call fetches event listings");
        events.Should().HaveCountGreaterThanOrEqualTo(1);
    }

    /// <summary>
    /// A 403 response from the RA GraphQL endpoint yields nothing without throwing.
    /// </summary>
    [Fact]
    public async Task RaAdapter_YieldsNothing_On403()
    {
        var handler = new StubHttpMessageHandler()
            .OnUrlPattern(@"ra\.co/graphql", HttpStatusCode.Forbidden, "{}", "application/json");

        var adapter = BuildAdapter(handler);

        var act = async () => await CollectAsync(
            adapter.FetchAsync(Deadmau5WithSlug, OpenFilter, CancellationToken.None));

        var result = await act.Should().NotThrowAsync();
        result.Subject.Should().BeEmpty("403 from RA GraphQL means yield nothing");
    }

    /// <summary>
    /// When AcceptRaScrapeTos is false, IsConfigured returns false and FetchAsync yields nothing.
    /// </summary>
    [Fact]
    public async Task RaAdapter_SkippedWhenTosNotAccepted()
    {
        _cfg.AcceptRaScrapeTos = false;
        SetupPluginInstance(_cfg);

        var handler = new StubHttpMessageHandler()
            .AlwaysReturn(HttpStatusCode.OK, "{}", "application/json");

        var adapter = BuildAdapter(handler);
        var events = await CollectAsync(
            adapter.FetchAsync(Deadmau5WithSlug, OpenFilter, CancellationToken.None));

        events.Should().BeEmpty("adapter must yield nothing when ToS not accepted");
        handler.CallCount.Should().Be(0, "no HTTP calls should be made when ToS not accepted");
    }

    /// <summary>
    /// IsConfigured returns false when AcceptRaScrapeTos is false.
    /// </summary>
    [Fact]
    public void RaAdapter_IsConfigured_ReturnsFalse_WhenTosNotAccepted()
    {
        var cfgNoTos = new PluginConfiguration { AcceptRaScrapeTos = false };
        var handler  = new StubHttpMessageHandler();
        var adapter  = BuildAdapter(handler);

        adapter.IsConfigured(cfgNoTos).Should().BeFalse();
    }

    /// <summary>
    /// IsConfigured returns true when AcceptRaScrapeTos is true.
    /// </summary>
    [Fact]
    public void RaAdapter_IsConfigured_ReturnsTrue_WhenTosAccepted()
    {
        var cfgWithTos = new PluginConfiguration { AcceptRaScrapeTos = true };
        var handler    = new StubHttpMessageHandler();
        var adapter    = BuildAdapter(handler);

        adapter.IsConfigured(cfgWithTos).Should().BeTrue();
    }
}
