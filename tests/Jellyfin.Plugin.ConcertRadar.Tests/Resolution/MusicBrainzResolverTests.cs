using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Jellyfin.Plugin.ConcertRadar.Configuration;
using Jellyfin.Plugin.ConcertRadar.RateLimiting;
using Jellyfin.Plugin.ConcertRadar.Resolution;
using Jellyfin.Plugin.ConcertRadar.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.ConcertRadar.Tests.Resolution;

/// <summary>
/// Unit tests for <see cref="MusicBrainzResolver"/> using fake HTTP handlers.
/// No live network calls are made.
/// </summary>
public class MusicBrainzResolverTests
{
    private const string Mbid = "a74b1b7f-71a5-4011-9441-d0b5e4122711";

    private static readonly DateTimeOffset BaseTime =
        new DateTimeOffset(2025, 6, 15, 12, 0, 0, TimeSpan.Zero);

    // ── Helper: build a resolver with a stubbed HTTP handler ─────────────────

    /// <summary>
    /// Builds a <see cref="MusicBrainzResolver"/> whose HTTP calls go through
    /// <paramref name="handler"/>.  The rate limiter uses a very high token budget
    /// so it never blocks in unit tests.
    /// </summary>
    private static async Task<(MusicBrainzResolver resolver, TestDatabase db)> BuildAsync(
        StubHttpMessageHandler handler)
    {
        var clock = new TimeProviderStub(BaseTime);
        var db = await TestDatabase.CreateAsync(clock);

        // Config with a high per-second limit so the limiter never throttles in tests.
        var cfg = new PluginConfiguration
        {
            RateLimits = new List<SourceRateLimitEntry>
            {
                new()
                {
                    Source = "musicbrainz",
                    Config = new RateLimitConfig { RequestsPerSecond = 1000 },
                },
            },
        };

        var configProvider = new StubPluginConfigurationProvider(cfg);
        var rateLimiter = new HostRateLimiter(
            db.SourceState,
            configProvider,
            clock,
            NullLogger<HostRateLimiter>.Instance);

        var factory = BuildFactory(handler);
        var resolver = new MusicBrainzResolver(
            factory,
            rateLimiter,
            configProvider,
            NullLogger<MusicBrainzResolver>.Instance);

        return (resolver, db);
    }

    /// <summary>
    /// Wraps a <see cref="StubHttpMessageHandler"/> in an <see cref="IHttpClientFactory"/>
    /// substitute so every <c>CreateClient</c> call returns an <see cref="HttpClient"/>
    /// backed by the stub.
    /// </summary>
    private static IHttpClientFactory BuildFactory(StubHttpMessageHandler handler)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>())
               .Returns(_ => new HttpClient(handler, disposeHandler: false));
        return factory;
    }

    // ── ResolveMbidByName ─────────────────────────────────────────────────────

    [Fact]
    public async Task ResolveMbidByName_PicksHighestScoreAboveThreshold()
    {
        var body = FixtureLoader.LoadText("musicbrainz/search_multi_score.json");
        var handler = new StubHttpMessageHandler()
            .OnUrlPattern(@"musicbrainz\.org/ws/2/artist\?query=", HttpStatusCode.OK, body);

        var (resolver, db) = await BuildAsync(handler);
        await using (db)
        {
            var result = await resolver.ResolveMbidByNameAsync("Radiohead", CancellationToken.None);

            result.Should().Be("a74b1b7f-71a5-4011-9441-d0b5e4122711",
                because: "the highest-scoring candidate (95) is above the 85-point threshold");
        }
    }

    [Fact]
    public async Task ResolveMbidByName_ReturnsNullWhenAllBelowThreshold()
    {
        var body = FixtureLoader.LoadText("musicbrainz/search_all_low_score.json");
        var handler = new StubHttpMessageHandler()
            .OnUrlPattern(@"musicbrainz\.org/ws/2/artist\?query=", HttpStatusCode.OK, body);

        var (resolver, db) = await BuildAsync(handler);
        await using (db)
        {
            var result = await resolver.ResolveMbidByNameAsync("Radioheads", CancellationToken.None);

            result.Should().BeNull(
                because: "top score 60 is below the 85-point threshold");
        }
    }

    // ── FetchUrlRels ──────────────────────────────────────────────────────────

    [Fact]
    public async Task FetchUrlRels_ExtractsSongkickIdFromUrl()
    {
        var body = FixtureLoader.LoadText("musicbrainz/artist_with_songkick_rel.json");
        var handler = new StubHttpMessageHandler()
            .OnUrlPattern(@"musicbrainz\.org/ws/2/artist/" + Mbid, HttpStatusCode.OK, body);

        var (resolver, db) = await BuildAsync(handler);
        await using (db)
        {
            var ids = await resolver.FetchUrlRelsAsync(Mbid, CancellationToken.None);

            ids.Should().ContainKey("songkick");
            ids["songkick"].Should().Be("253846",
                because: "the URL https://www.songkick.com/artists/253846-radiohead contains ID 253846");
        }
    }

    [Fact]
    public async Task FetchUrlRels_ExtractsDiceSlug()
    {
        var body = FixtureLoader.LoadText("musicbrainz/artist_with_dice_ra_rels.json");
        var handler = new StubHttpMessageHandler()
            .OnUrlPattern(@"musicbrainz\.org/ws/2/artist/" + Mbid, HttpStatusCode.OK, body);

        var (resolver, db) = await BuildAsync(handler);
        await using (db)
        {
            var ids = await resolver.FetchUrlRelsAsync(Mbid, CancellationToken.None);

            ids.Should().ContainKey("dice");
            ids["dice"].Should().Be("radiohead-abc123",
                because: "dice.fm/artist/radiohead-abc123 should yield slug 'radiohead-abc123'");
        }
    }

    [Fact]
    public async Task FetchUrlRels_ExtractsRaSlug()
    {
        var body = FixtureLoader.LoadText("musicbrainz/artist_with_dice_ra_rels.json");
        var handler = new StubHttpMessageHandler()
            .OnUrlPattern(@"musicbrainz\.org/ws/2/artist/" + Mbid, HttpStatusCode.OK, body);

        var (resolver, db) = await BuildAsync(handler);
        await using (db)
        {
            var ids = await resolver.FetchUrlRelsAsync(Mbid, CancellationToken.None);

            ids.Should().ContainKey("ra");
            ids["ra"].Should().Be("radiohead",
                because: "ra.co/dj/radiohead should yield slug 'radiohead'");
        }
    }

    [Fact]
    public async Task FetchUrlRels_SetsBandsintownMbidIdentifier()
    {
        var body = FixtureLoader.LoadText("musicbrainz/artist_with_bandsintown_rel.json");
        var handler = new StubHttpMessageHandler()
            .OnUrlPattern(@"musicbrainz\.org/ws/2/artist/" + Mbid, HttpStatusCode.OK, body);

        var (resolver, db) = await BuildAsync(handler);
        await using (db)
        {
            var ids = await resolver.FetchUrlRelsAsync(Mbid, CancellationToken.None);

            ids.Should().ContainKey("bandsintown");
            ids["bandsintown"].Should().Be($"mbid_{Mbid}",
                because: "Bandsintown's canonical identifier is mbid_<uuid>");
        }
    }

    [Fact]
    public async Task FetchUrlRels_IgnoresUnknownRelationTypes()
    {
        var body = FixtureLoader.LoadText("musicbrainz/artist_unknown_rels.json");
        var handler = new StubHttpMessageHandler()
            .OnUrlPattern(@"musicbrainz\.org/ws/2/artist/" + Mbid, HttpStatusCode.OK, body);

        var (resolver, db) = await BuildAsync(handler);
        await using (db)
        {
            var ids = await resolver.FetchUrlRelsAsync(Mbid, CancellationToken.None);

            ids.Should().BeEmpty(
                because: "wikipedia/homepage/youtube relations are not tracked source identifiers");
        }
    }

    // ── User-Agent header ─────────────────────────────────────────────────────

    [Fact]
    public async Task Request_SetsUserAgentHeader()
    {
        var body = FixtureLoader.LoadText("musicbrainz/artist_unknown_rels.json");
        HttpRequestMessage? captured = null;

        var handler = new StubHttpMessageHandler()
            .OnUrlPattern(@"musicbrainz\.org", (req, _) =>
            {
                captured = req;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                };
            });

        var (resolver, db) = await BuildAsync(handler);
        await using (db)
        {
            await resolver.FetchUrlRelsAsync(Mbid, CancellationToken.None);

            captured.Should().NotBeNull();
            var ua = captured!.Headers.UserAgent.ToString();
            ua.Should().StartWith("JellyfinConcertRadar/",
                because: "MusicBrainz ToS requires a descriptive User-Agent starting with the application name");
        }
    }

    // ── Retry on 429 ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Retries_On429_WithBackoff()
    {
        var goodBody = FixtureLoader.LoadText("musicbrainz/search_multi_score.json");

        // First 2 calls return 429; 3rd returns 200.
        int callIndex = 0;
        var handler = new StubHttpMessageHandler()
            .OnUrlPattern(@"musicbrainz\.org", (_, _) =>
            {
                int idx = Interlocked.Increment(ref callIndex) - 1;
                if (idx < 2)
                    return new HttpResponseMessage(HttpStatusCode.TooManyRequests);

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(goodBody, Encoding.UTF8, "application/json"),
                };
            });

        var (resolver, db) = await BuildAsync(handler);
        await using (db)
        {
            // Should ultimately succeed after retries. Retry delays are 200ms + 800ms, so
            // use a generous timeout.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var result = await resolver.ResolveMbidByNameAsync("Radiohead", cts.Token);

            result.Should().Be("a74b1b7f-71a5-4011-9441-d0b5e4122711",
                because: "after 2 x 429 retries, the 3rd attempt should succeed and return the MBID");
            callIndex.Should().Be(3,
                because: "exactly 3 HTTP calls should be made (2 x 429 + 1 success)");
        }
    }
}
