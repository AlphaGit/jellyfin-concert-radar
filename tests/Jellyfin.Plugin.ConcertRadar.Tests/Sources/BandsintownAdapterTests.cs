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
/// Unit tests for <see cref="BandsintownAdapter"/>.
/// Tests use <see cref="StubHttpMessageHandler"/> so no live network calls are made.
/// Placed in <see cref="PluginInstanceCollection"/> to prevent parallel mutation of
/// the static <c>Plugin.Instance</c> field.
/// </summary>
[Collection(PluginInstanceCollection.Name)]
public sealed class BandsintownAdapterTests : IAsyncLifetime
{
    private static readonly string RadioheadMbid = "a74b1b7f-71a5-4011-9441-d0b5e4122711";

    private static readonly ArtistRef RadioheadWithMbid = new ArtistRef(
        Name: "Radiohead",
        Mbid: RadioheadMbid,
        ExternalIds: new ReadOnlyDictionary<string, string>(new Dictionary<string, string>()));

    private static readonly ArtistRef RadioheadNoMbid = new ArtistRef(
        Name: "Radiohead",
        Mbid: null,
        ExternalIds: new ReadOnlyDictionary<string, string>(new Dictionary<string, string>()));

    private static readonly ArtistRef RadioheadWithCachedExtId = new ArtistRef(
        Name: "Radiohead",
        Mbid: RadioheadMbid,
        ExternalIds: new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string> { ["bandsintown"] = "mbid_cached-override-id" }));

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
            BandsintownAppId = "test-app-id",
            RateLimits = new List<SourceRateLimitEntry>
            {
                // Permissive rate limit so tests don't wait.
                new SourceRateLimitEntry
                {
                    Source = "bandsintown",
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

    private BandsintownAdapter BuildAdapter(StubHttpMessageHandler handler)
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

        return new BandsintownAdapter(
            factory,
            rateLimiter,
            _db.SourceState,
            configProvider,
            TimeProvider.System,
            NullLogger<BandsintownAdapter>.Instance);
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
    /// When an MBID is present and no cached ext_id exists, the URL path contains mbid_&lt;uuid&gt;.
    /// </summary>
    [Fact]
    public async Task BandsintownAdapter_UsesMbidIdentifier_WhenPresent()
    {
        string body = FixtureLoader.LoadText("bandsintown/events_empty.json");
        string? capturedUrl = null;

        var handler = new StubHttpMessageHandler()
            .OnUrlPattern(@"artists/", (req, _) =>
            {
                capturedUrl = req.RequestUri?.ToString();
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new System.Net.Http.StringContent(
                        body, System.Text.Encoding.UTF8, "application/json"),
                };
            });

        var adapter = BuildAdapter(handler);
        await CollectAsync(adapter.FetchAsync(RadioheadWithMbid, OpenFilter, CancellationToken.None));

        capturedUrl.Should().Contain($"mbid_{RadioheadMbid}",
            "adapter should synthesize 'mbid_<uuid>' from the artist MBID");
    }

    /// <summary>
    /// When no MBID is present, the URL path contains the URL-encoded artist name.
    /// </summary>
    [Fact]
    public async Task BandsintownAdapter_FallsBackToEncodedName_WhenNoMbid()
    {
        string body = FixtureLoader.LoadText("bandsintown/events_empty.json");
        string? capturedUrl = null;

        var handler = new StubHttpMessageHandler()
            .OnUrlPattern(@"artists/", (req, _) =>
            {
                capturedUrl = req.RequestUri?.ToString();
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new System.Net.Http.StringContent(
                        body, System.Text.Encoding.UTF8, "application/json"),
                };
            });

        var adapter = BuildAdapter(handler);
        await CollectAsync(adapter.FetchAsync(RadioheadNoMbid, OpenFilter, CancellationToken.None));

        capturedUrl.Should().Contain("Radiohead",
            "adapter should URL-encode the artist name when MBID is absent");
        capturedUrl.Should().NotContain("mbid_",
            "no MBID should mean no mbid_ prefix");
    }

    /// <summary>
    /// When ext_ids["bandsintown"] is set, that value is used verbatim in the URL path
    /// (no re-synthesis from Mbid).
    /// </summary>
    [Fact]
    public async Task BandsintownAdapter_UsesExternalIdWhenCached()
    {
        string body = FixtureLoader.LoadText("bandsintown/events_empty.json");
        string? capturedUrl = null;

        var handler = new StubHttpMessageHandler()
            .OnUrlPattern(@"artists/", (req, _) =>
            {
                capturedUrl = req.RequestUri?.ToString();
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new System.Net.Http.StringContent(
                        body, System.Text.Encoding.UTF8, "application/json"),
                };
            });

        var adapter = BuildAdapter(handler);
        await CollectAsync(
            adapter.FetchAsync(RadioheadWithCachedExtId, OpenFilter, CancellationToken.None));

        capturedUrl.Should().Contain("mbid_cached-override-id",
            "adapter must use the cached ext_id verbatim, not rebuild from Mbid");
        capturedUrl.Should().NotContain(RadioheadMbid,
            "the artist MBID should not appear in the URL when a cached id is present");
    }

    /// <summary>
    /// The happy-path fixture yields 3 events.
    /// </summary>
    [Fact]
    public async Task BandsintownAdapter_ParsesEventsArray()
    {
        string body = FixtureLoader.LoadText("bandsintown/events_happy_path.json");

        var handler = new StubHttpMessageHandler()
            .OnUrlPattern(@"artists/", HttpStatusCode.OK, body);

        var adapter = BuildAdapter(handler);
        var events = await CollectAsync(
            adapter.FetchAsync(RadioheadWithMbid, OpenFilter, CancellationToken.None));

        events.Should().HaveCount(3);
    }

    /// <summary>
    /// Venue fields (name, city, region, country, lat, lon) are mapped from venue.*.
    /// </summary>
    [Fact]
    public async Task BandsintownAdapter_MapsVenueFields()
    {
        string body = FixtureLoader.LoadText("bandsintown/events_happy_path.json");

        var handler = new StubHttpMessageHandler()
            .OnUrlPattern(@"artists/", HttpStatusCode.OK, body);

        var adapter = BuildAdapter(handler);
        var events = await CollectAsync(
            adapter.FetchAsync(RadioheadWithMbid, OpenFilter, CancellationToken.None));

        var first = events[0];
        first.VenueName.Should().Be("O2 Arena");
        first.City.Should().Be("London");
        first.Region.Should().Be("England");
        first.Country.Should().Be("United Kingdom");
        first.Lat.Should().NotBeNull();
        first.Lon.Should().NotBeNull();
    }

    /// <summary>
    /// The lineup field preserves the order from the fixture (Radiohead, Thom Yorke for BIT-001).
    /// </summary>
    [Fact]
    public async Task BandsintownAdapter_MapsLineupOrder()
    {
        string body = FixtureLoader.LoadText("bandsintown/events_happy_path.json");

        var handler = new StubHttpMessageHandler()
            .OnUrlPattern(@"artists/", HttpStatusCode.OK, body);

        var adapter = BuildAdapter(handler);
        var events = await CollectAsync(
            adapter.FetchAsync(RadioheadWithMbid, OpenFilter, CancellationToken.None));

        // BIT-001 has lineup: ["Radiohead", "Thom Yorke"]
        events[0].Lineup.Should().ContainInOrder("Radiohead", "Thom Yorke");
        // BIT-003 has lineup: ["Radiohead", "PJ Harvey", "Portishead"]
        events[2].Lineup.Should().ContainInOrder("Radiohead", "PJ Harvey", "Portishead");
    }

    /// <summary>
    /// When an offer of type Tickets with status "available" exists, TicketUrl is set to it.
    /// </summary>
    [Fact]
    public async Task BandsintownAdapter_MapsTicketOffer_WhenAvailable()
    {
        string body = FixtureLoader.LoadText("bandsintown/events_happy_path.json");

        var handler = new StubHttpMessageHandler()
            .OnUrlPattern(@"artists/", HttpStatusCode.OK, body);

        var adapter = BuildAdapter(handler);
        var events = await CollectAsync(
            adapter.FetchAsync(RadioheadWithMbid, OpenFilter, CancellationToken.None));

        // BIT-001 has offer status "available" → TicketUrl should be the offer URL.
        events[0].TicketUrl.Should().Be("https://www.ticketweb.uk/event/BIT-001");
    }

    /// <summary>
    /// When the offer status is "sold out", IsSoldOut is true.
    /// </summary>
    [Fact]
    public async Task BandsintownAdapter_SetsIsSoldOut_WhenOfferSoldOut()
    {
        string body = FixtureLoader.LoadText("bandsintown/events_happy_path.json");

        var handler = new StubHttpMessageHandler()
            .OnUrlPattern(@"artists/", HttpStatusCode.OK, body);

        var adapter = BuildAdapter(handler);
        var events = await CollectAsync(
            adapter.FetchAsync(RadioheadWithMbid, OpenFilter, CancellationToken.None));

        // BIT-002 has offer status "sold out".
        events[1].IsSoldOut.Should().BeTrue("BIT-002 offer is 'sold out'");
        // BIT-001 has offer status "available".
        events[0].IsSoldOut.Should().BeFalse("BIT-001 offer is 'available'");
    }

    /// <summary>
    /// A 404 response means the artist is not found — stream should be empty, no exception.
    /// </summary>
    [Fact]
    public async Task BandsintownAdapter_YieldsNothingOn404()
    {
        var handler = new StubHttpMessageHandler()
            .OnUrlPattern(@"artists/", HttpStatusCode.NotFound, string.Empty);

        var adapter = BuildAdapter(handler);

        var act = async () =>
        {
            var events = await CollectAsync(
                adapter.FetchAsync(RadioheadWithMbid, OpenFilter, CancellationToken.None));
            return events;
        };

        var events = await act.Should().NotThrowAsync();
        events.Subject.Should().BeEmpty("404 = artist not found, not an error");
    }

    /// <summary>
    /// When the server returns 500 twice then 200, the adapter succeeds after 3 attempts.
    /// Retry-After: 0 is used to keep the test fast.
    /// </summary>
    [Fact]
    public async Task BandsintownAdapter_Retries_On5xx()
    {
        string body = FixtureLoader.LoadText("bandsintown/events_happy_path.json");
        int callCount = 0;

        var handler = new StubHttpMessageHandler()
            .OnUrlPattern(@"artists/", (_, _) =>
            {
                callCount++;
                if (callCount <= 2)
                {
                    var resp500 = new HttpResponseMessage(HttpStatusCode.InternalServerError)
                    {
                        Content = new System.Net.Http.StringContent(
                            "{}", System.Text.Encoding.UTF8, "application/json"),
                    };
                    resp500.Headers.RetryAfter =
                        new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
                    return resp500;
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new System.Net.Http.StringContent(
                        body, System.Text.Encoding.UTF8, "application/json"),
                };
            });

        var adapter = BuildAdapter(handler);
        var events = await CollectAsync(
            adapter.FetchAsync(RadioheadWithMbid, OpenFilter, CancellationToken.None));

        callCount.Should().Be(3, "two 500s then one 200 = 3 total attempts");
        events.Should().HaveCount(3);
    }
}
