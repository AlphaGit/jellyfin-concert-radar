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
/// Contract (fixture-backed) tests for <see cref="DiceScrapeAdapter"/>.
/// Validates that the __NEXT_DATA__ extraction and event mapping work against the recorded HTML fixture.
/// These tests are drift detectors — if Dice changes its page structure, these break first.
/// </summary>
[Collection(PluginInstanceCollection.Name)]
public sealed class DiceScrapeAdapterTests : IAsyncLifetime
{
    private static readonly ArtistRef Deadmau5WithSlug = new ArtistRef(
        Name: "deadmau5",
        Mbid: null,
        ExternalIds: new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string> { ["dice"] = "deadmau5" }));

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
            AcceptDiceScrapeTos = true,
            EnabledSources = new List<string> { "dice" },
            RateLimits = new List<SourceRateLimitEntry>
            {
                new SourceRateLimitEntry
                {
                    Source = "dice",
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

    private DiceScrapeAdapter BuildAdapter(StubHttpMessageHandler handler)
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

        return new DiceScrapeAdapter(
            factory,
            rateLimiter,
            _db.Artists,
            _db.SourceState,
            configProvider,
            TimeProvider.System,
            NullLogger<DiceScrapeAdapter>.Instance);
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
    public async Task DiceAdapter_HappyPathFixture_ProducesEvents()
    {
        string artistPageHtml = FixtureLoader.LoadText("dice/artist_page.html");

        var handler = new StubHttpMessageHandler()
            .OnUrlPattern(@"dice\.fm/artist/", HttpStatusCode.OK, artistPageHtml, "text/html");

        var adapter = BuildAdapter(handler);

        var act = async () => await CollectAsync(
            adapter.FetchAsync(Deadmau5WithSlug, OpenFilter, CancellationToken.None));

        var events = await act.Should().NotThrowAsync();
        events.Subject.Should().HaveCountGreaterThanOrEqualTo(1,
            "happy-path fixture contains event entries in __NEXT_DATA__");
    }

    /// <summary>
    /// Mandatory fields (SourceUrl, EventDateTime, ArtistName) are populated for every event.
    /// </summary>
    [Fact]
    public async Task DiceAdapter_MandatoryFieldsPopulated()
    {
        string artistPageHtml = FixtureLoader.LoadText("dice/artist_page.html");

        var handler = new StubHttpMessageHandler()
            .OnUrlPattern(@"dice\.fm/artist/", HttpStatusCode.OK, artistPageHtml, "text/html");

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
    /// __NEXT_DATA__ blob from the artist_page fixture is extracted and contains events.
    /// </summary>
    [Fact]
    public async Task DiceAdapter_ExtractsNextDataBlob()
    {
        string artistPageHtml = FixtureLoader.LoadText("dice/artist_page.html");

        var handler = new StubHttpMessageHandler()
            .OnUrlPattern(@"dice\.fm/artist/", HttpStatusCode.OK, artistPageHtml, "text/html");

        var adapter = BuildAdapter(handler);
        var events = await CollectAsync(
            adapter.FetchAsync(Deadmau5WithSlug, OpenFilter, CancellationToken.None));

        events.Should().HaveCount(2, "fixture has two events in props.pageProps.artist.events");
    }

    /// <summary>
    /// Events are parsed: sold-out flag, venue, city, lineup.
    /// </summary>
    [Fact]
    public async Task DiceAdapter_ParsesEventsFromNextData()
    {
        string artistPageHtml = FixtureLoader.LoadText("dice/artist_page.html");

        var handler = new StubHttpMessageHandler()
            .OnUrlPattern(@"dice\.fm/artist/", HttpStatusCode.OK, artistPageHtml, "text/html");

        var adapter = BuildAdapter(handler);
        var events = await CollectAsync(
            adapter.FetchAsync(Deadmau5WithSlug, OpenFilter, CancellationToken.None));

        // First event: available tickets → not sold out.
        events[0].IsSoldOut.Should().BeFalse("dice-evt-001 has tickets.available=true");
        events[0].VenueName.Should().Be("Echostage");
        events[0].City.Should().Be("Washington DC");
        events[0].ArtistName.Should().Be("deadmau5");

        // Second event: tickets not available → sold out.
        events[1].IsSoldOut.Should().BeTrue("dice-evt-002 has tickets.available=false");
        events[1].Lineup.Should().ContainInOrder("deadmau5", "Eric Prydz");
    }

    /// <summary>
    /// When the artist slug is resolved from the search page, the adapter uses it for the
    /// subsequent artist page request.
    /// </summary>
    [Fact]
    public async Task DiceAdapter_ResolvesSlugFromSearchPage()
    {
        string searchPageHtml  = FixtureLoader.LoadText("dice/search_page.html");
        string artistPageHtml  = FixtureLoader.LoadText("dice/artist_page.html");

        int artistPageHits = 0;
        var handler = new StubHttpMessageHandler()
            .OnUrlPattern(@"dice\.fm/search", HttpStatusCode.OK, searchPageHtml, "text/html")
            .OnUrlPattern(@"dice\.fm/artist/", (_, _) =>
            {
                artistPageHits++;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(artistPageHtml, System.Text.Encoding.UTF8, "text/html"),
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

        artistPageHits.Should().Be(1, "slug resolved from search → artist page fetched once");
        events.Should().HaveCountGreaterThanOrEqualTo(1);
    }

    /// <summary>
    /// A 403 Forbidden (Cloudflare-style) yields nothing without throwing.
    /// </summary>
    [Fact]
    public async Task DiceAdapter_YieldsNothing_On403()
    {
        var handler = new StubHttpMessageHandler()
            .OnUrlPattern(@"dice\.fm", HttpStatusCode.Forbidden, "<html>Forbidden</html>", "text/html");

        var adapter = BuildAdapter(handler);

        var act = async () => await CollectAsync(
            adapter.FetchAsync(Deadmau5WithSlug, OpenFilter, CancellationToken.None));

        var result = await act.Should().NotThrowAsync();
        result.Subject.Should().BeEmpty("403 Cloudflare block means yield nothing");
    }

    /// <summary>
    /// When AcceptDiceScrapeTos is false, IsConfigured returns false and FetchAsync yields nothing.
    /// </summary>
    [Fact]
    public async Task DiceAdapter_SkippedWhenTosNotAccepted()
    {
        // Reconfigure: ToS NOT accepted.
        _cfg.AcceptDiceScrapeTos = false;
        SetupPluginInstance(_cfg);

        // Handler should never be called.
        var handler = new StubHttpMessageHandler()
            .AlwaysReturn(HttpStatusCode.OK, "<html></html>", "text/html");

        var adapter = BuildAdapter(handler);
        var events = await CollectAsync(
            adapter.FetchAsync(Deadmau5WithSlug, OpenFilter, CancellationToken.None));

        events.Should().BeEmpty("adapter must yield nothing when ToS not accepted");
        handler.CallCount.Should().Be(0, "no HTTP calls should be made when ToS not accepted");
    }

    /// <summary>
    /// IsConfigured returns false when AcceptDiceScrapeTos is false.
    /// </summary>
    [Fact]
    public void DiceAdapter_IsConfigured_ReturnsFalse_WhenTosNotAccepted()
    {
        var cfgNoTos = new PluginConfiguration { AcceptDiceScrapeTos = false };
        var handler  = new StubHttpMessageHandler();
        var adapter  = BuildAdapter(handler);

        adapter.IsConfigured(cfgNoTos).Should().BeFalse();
    }

    /// <summary>
    /// IsConfigured returns true when AcceptDiceScrapeTos is true.
    /// </summary>
    [Fact]
    public void DiceAdapter_IsConfigured_ReturnsTrue_WhenTosAccepted()
    {
        var cfgWithTos = new PluginConfiguration { AcceptDiceScrapeTos = true };
        var handler    = new StubHttpMessageHandler();
        var adapter    = BuildAdapter(handler);

        adapter.IsConfigured(cfgWithTos).Should().BeTrue();
    }
}
