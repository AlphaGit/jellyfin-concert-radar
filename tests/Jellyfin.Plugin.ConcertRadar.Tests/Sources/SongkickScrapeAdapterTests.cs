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
/// Contract (fixture-backed) tests for <see cref="SongkickScrapeAdapter"/>.
/// Validates the parser does not throw and produces correct output on the recorded fixtures.
/// These are drift detectors: if Songkick changes its HTML DOM, these break first.
/// </summary>
[Collection(PluginInstanceCollection.Name)]
public sealed class SongkickScrapeAdapterTests : IAsyncLifetime
{
    private static readonly ArtistRef RadioheadWithSongkickId = new ArtistRef(
        Name: "Radiohead",
        Mbid: "a74b1b7f-71a5-4011-9441-d0b5e4122711",
        ExternalIds: new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string> { ["songkick"] = "253846-radiohead" }));

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
            EnabledSources = new List<string> { "songkick" },
            RateLimits = new List<SourceRateLimitEntry>
            {
                new SourceRateLimitEntry
                {
                    Source = "songkick",
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

    private SongkickScrapeAdapter BuildAdapter(StubHttpMessageHandler handler)
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

        return new SongkickScrapeAdapter(
            factory,
            rateLimiter,
            _db.Artists,
            _db.SourceState,
            configProvider,
            TimeProvider.System,
            NullLogger<SongkickScrapeAdapter>.Instance);
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
    public async Task SongkickAdapter_HappyPathFixture_ProducesEvents()
    {
        string artistPageHtml = FixtureLoader.LoadText("songkick/artist_page.html");

        var handler = new StubHttpMessageHandler()
            .OnUrlPattern(@"songkick\.com/artists/", HttpStatusCode.OK, artistPageHtml, "text/html");

        var adapter = BuildAdapter(handler);

        var act = async () => await CollectAsync(
            adapter.FetchAsync(RadioheadWithSongkickId, OpenFilter, CancellationToken.None));

        var events = await act.Should().NotThrowAsync();
        events.Subject.Should().HaveCountGreaterThanOrEqualTo(1,
            "happy-path fixture contains event listings");
    }

    /// <summary>
    /// SourceUrl, EventDateTime, and ArtistName are populated for every event from the fixture.
    /// </summary>
    [Fact]
    public async Task SongkickAdapter_MandatoryFieldsPopulated()
    {
        string artistPageHtml = FixtureLoader.LoadText("songkick/artist_page.html");

        var handler = new StubHttpMessageHandler()
            .OnUrlPattern(@"songkick\.com/artists/", HttpStatusCode.OK, artistPageHtml, "text/html");

        var adapter = BuildAdapter(handler);
        var events = await CollectAsync(
            adapter.FetchAsync(RadioheadWithSongkickId, OpenFilter, CancellationToken.None));

        events.Should().AllSatisfy(e =>
        {
            e.SourceUrl.Should().NotBeNullOrWhiteSpace("SourceUrl is mandatory");
            e.EventDateTime.Should().NotBe(default, "EventDateTime is mandatory");
            e.ArtistName.Should().NotBeNullOrWhiteSpace("ArtistName is mandatory");
        });
    }

    /// <summary>
    /// The datetime attribute from &lt;time datetime="..."&gt; is extracted correctly.
    /// </summary>
    [Fact]
    public async Task SongkickAdapter_ExtractsDateFromTimeDatetimeAttribute()
    {
        string artistPageHtml = FixtureLoader.LoadText("songkick/artist_page.html");

        var handler = new StubHttpMessageHandler()
            .OnUrlPattern(@"songkick\.com/artists/", HttpStatusCode.OK, artistPageHtml, "text/html");

        var adapter = BuildAdapter(handler);
        var events = await CollectAsync(
            adapter.FetchAsync(RadioheadWithSongkickId, OpenFilter, CancellationToken.None));

        // First event: 2025-09-20T20:00:00+01:00
        events[0].EventDateTime.Year.Should().Be(2025);
        events[0].EventDateTime.Month.Should().Be(9);
        events[0].EventDateTime.Day.Should().Be(20);
    }

    /// <summary>
    /// Venue name, city, and country are scraped from the correct selectors.
    /// </summary>
    [Fact]
    public async Task SongkickAdapter_ExtractsVenueCityCountry()
    {
        string artistPageHtml = FixtureLoader.LoadText("songkick/artist_page.html");

        var handler = new StubHttpMessageHandler()
            .OnUrlPattern(@"songkick\.com/artists/", HttpStatusCode.OK, artistPageHtml, "text/html");

        var adapter = BuildAdapter(handler);
        var events = await CollectAsync(
            adapter.FetchAsync(RadioheadWithSongkickId, OpenFilter, CancellationToken.None));

        var first = events[0];
        first.VenueName.Should().Be("O2 Arena");
        first.City.Should().Be("London");
        first.Country.Should().Be("United Kingdom");
    }

    /// <summary>
    /// Each event has its own SourceUrl derived from the href of the event link.
    /// </summary>
    [Fact]
    public async Task SongkickAdapter_EmitsSourceUrlPerEvent()
    {
        string artistPageHtml = FixtureLoader.LoadText("songkick/artist_page.html");

        var handler = new StubHttpMessageHandler()
            .OnUrlPattern(@"songkick\.com/artists/", HttpStatusCode.OK, artistPageHtml, "text/html");

        var adapter = BuildAdapter(handler);
        var events = await CollectAsync(
            adapter.FetchAsync(RadioheadWithSongkickId, OpenFilter, CancellationToken.None));

        events[0].SourceUrl.Should().Contain("40123456");
        events[1].SourceUrl.Should().Contain("40123457");
    }

    /// <summary>
    /// When the search page is fetched first (no cached ext_id), the adapter resolves the
    /// artist URL from the search fixture then fetches the artist page.
    /// </summary>
    [Fact]
    public async Task SongkickAdapter_ResolvesArtistViaSearch_WhenNoExtId()
    {
        string searchPageHtml  = FixtureLoader.LoadText("songkick/search_page.html");
        string artistPageHtml  = FixtureLoader.LoadText("songkick/artist_page.html");

        var handler = new StubHttpMessageHandler()
            .OnUrlPattern(@"search\?", HttpStatusCode.OK, searchPageHtml, "text/html")
            .OnUrlPattern(@"songkick\.com/artists/", HttpStatusCode.OK, artistPageHtml, "text/html");

        // Insert artist row so SetExternalIdsAsync can store the persisted slug.
        await _db.Artists.UpsertFromLibraryAsync(
            new[]
            {
                new LibraryArtist(
                    Mbid: RadioheadNoExtId.Mbid,
                    Name: RadioheadNoExtId.Name,
                    JellyfinItemId: Guid.NewGuid()),
            },
            CancellationToken.None);

        var adapter = BuildAdapter(handler);
        var events = await CollectAsync(
            adapter.FetchAsync(RadioheadNoExtId, OpenFilter, CancellationToken.None));

        events.Should().HaveCountGreaterThanOrEqualTo(1,
            "search page resolves to artist page which has events");
    }

    /// <summary>
    /// A 403 Forbidden response (Cloudflare-style) yields nothing without throwing.
    /// </summary>
    [Fact]
    public async Task SongkickAdapter_YieldsNothing_On403()
    {
        var handler = new StubHttpMessageHandler()
            .OnUrlPattern(@"songkick\.com", HttpStatusCode.Forbidden, "<html>Forbidden</html>", "text/html");

        var adapter = BuildAdapter(handler);

        var act = async () => await CollectAsync(
            adapter.FetchAsync(RadioheadWithSongkickId, OpenFilter, CancellationToken.None));

        var result = await act.Should().NotThrowAsync();
        result.Subject.Should().BeEmpty("403 means Cloudflare block; yield nothing");
    }
}
