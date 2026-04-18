using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel.DataAnnotations;
using FluentAssertions;
using Jellyfin.Plugin.ConcertRadar.Model;
using Jellyfin.Plugin.ConcertRadar.Normalization;
using Xunit;

namespace Jellyfin.Plugin.ConcertRadar.Tests.Normalization;

/// <summary>
/// Unit tests for <see cref="Normalizer"/>.  No IO — all methods are pure.
/// </summary>
public class NormalizerTests
{
    // ── Shared fixtures ───────────────────────────────────────────────────────

    private static readonly DateTimeOffset Now =
        new DateTimeOffset(2025, 6, 15, 12, 0, 0, TimeSpan.Zero);

    private static readonly ArtistRef Artist = new ArtistRef(
        Name: "Radiohead",
        Mbid: "a74b1b7f-71a5-4011-9441-d0b5e4122711",
        ExternalIds: new ReadOnlyDictionary<string, string>(new Dictionary<string, string>()));

    private static readonly SourceFilter PassAllFilter = new SourceFilter(
        Locations:        Array.Empty<Jellyfin.Plugin.ConcertRadar.Configuration.LocationFilter>(),
        CountryAllowlist: new HashSet<string>(),
        GenreAllowlist:   new HashSet<string>(),
        MinDate:          DateTimeOffset.MinValue,
        MaxDate:          DateTimeOffset.MaxValue,
        SkipFestivals:    false,
        SkipSoldOut:      false);

    private static RawEvent ValidRaw(
        string sourceEventId = "evt-001",
        string sourceUrl = "https://ticketmaster.com/event/evt-001",
        string artistName = "Radiohead",
        DateTimeOffset? eventDateTime = null,
        string? country = "US",
        bool isFestival = false,
        bool isSoldOut = false,
        IReadOnlyList<string>? lineup = null)
        => new RawEvent(
            SourceEventId: sourceEventId,
            SourceUrl:     sourceUrl,
            ArtistName:    artistName,
            EventDateTime: eventDateTime ?? Now.AddDays(10),
            VenueName:     "Madison Square Garden",
            VenueAddress:  "4 Pennsylvania Plaza",
            City:          "New York",
            Region:        "NY",
            Country:       country,
            Lat:           40.7505,
            Lon:           -73.9934,
            Lineup:        lineup ?? new List<string> { "Radiohead", "Portishead" },
            TicketUrl:     "https://ticketmaster.com/event/evt-001/buy",
            PriceMin:      50m,
            PriceMax:      150m,
            Currency:      "USD",
            OnSaleAt:      Now.AddDays(-5),
            IsFestival:    isFestival,
            IsSoldOut:     isSoldOut);

    // ── Required-field mapping ────────────────────────────────────────────────

    [Fact]
    public void Normalize_MapsAllRequiredFields()
    {
        var raw = ValidRaw();

        var record = Normalizer.Normalize(raw, "ticketmaster", Artist, PassAllFilter, Now);

        record.Should().NotBeNull();
        record!.Source.Should().Be("ticketmaster");
        record.SourceEventId.Should().Be("evt-001");
        record.SourceUrl.Should().Be("https://ticketmaster.com/event/evt-001");
        record.ArtistMbid.Should().Be("a74b1b7f-71a5-4011-9441-d0b5e4122711");
        record.ArtistName.Should().Be("Radiohead");
        record.VenueName.Should().Be("Madison Square Garden");
        record.City.Should().Be("New York");
        record.Region.Should().Be("NY");
        record.Country.Should().Be("US");
        record.Lat.Should().Be(40.7505);
        record.Lon.Should().Be(-73.9934);
        record.Currency.Should().Be("USD");
        record.FetchedAt.Should().Be(Now);
        record.LastSeenAt.Should().Be(Now);
    }

    // ── Mandatory-field validation ────────────────────────────────────────────

    [Fact]
    public void Normalize_RejectsMissingSourceUrl()
    {
        var raw = ValidRaw(sourceUrl: "");

        var act = () => Normalizer.Normalize(raw, "ticketmaster", Artist, PassAllFilter, Now);

        act.Should().Throw<ValidationException>()
            .WithMessage("*SourceUrl*");
    }

    [Fact]
    public void Normalize_RejectsMissingEventDateTime()
    {
        // The normalizer checks EventDateTime == default(DateTimeOffset) = DateTimeOffset.MinValue.
        // We must construct the RawEvent directly with that sentinel value because the
        // ValidRaw helper would substitute Now.AddDays(10) for a null parameter.
        var raw = new RawEvent(
            SourceEventId: "evt-001",
            SourceUrl:     "https://ticketmaster.com/event/evt-001",
            ArtistName:    "Radiohead",
            EventDateTime: default,  // DateTimeOffset.MinValue — the sentinel
            VenueName:     null,
            VenueAddress:  null,
            City:          null,
            Region:        null,
            Country:       "US",
            Lat:           null,
            Lon:           null,
            Lineup:        Array.Empty<string>(),
            TicketUrl:     null,
            PriceMin:      null,
            PriceMax:      null,
            Currency:      null,
            OnSaleAt:      null,
            IsFestival:    false,
            IsSoldOut:     false);

        var act = () => Normalizer.Normalize(raw, "ticketmaster", Artist, PassAllFilter, Now);

        act.Should().Throw<ValidationException>()
            .WithMessage("*EventDateTime*");
    }

    [Fact]
    public void Normalize_RejectsMissingArtistName()
    {
        var raw = ValidRaw(artistName: "  ");

        var act = () => Normalizer.Normalize(raw, "ticketmaster", Artist, PassAllFilter, Now);

        act.Should().Throw<ValidationException>()
            .WithMessage("*ArtistName*");
    }

    // ── DateTimeOffset handling ───────────────────────────────────────────────

    [Fact]
    public void Normalize_ParsesIsoDateTimeWithOffset()
    {
        // +05:30 (India Standard Time)
        var offset = new DateTimeOffset(2025, 11, 20, 21, 30, 0, TimeSpan.FromHours(5.5));
        var raw = ValidRaw(eventDateTime: offset);

        var record = Normalizer.Normalize(raw, "ticketmaster", Artist, PassAllFilter, Now);

        record.Should().NotBeNull();
        record!.EventDateTime.Offset.Should().Be(TimeSpan.FromHours(5.5),
            because: "the non-UTC offset must be preserved through normalization");
        record.EventDateTime.Should().Be(offset);
    }

    [Fact]
    public void Normalize_ParsesDateTimeWithZuluSuffix()
    {
        var utcTime = new DateTimeOffset(2025, 11, 20, 20, 0, 0, TimeSpan.Zero);
        var raw = ValidRaw(eventDateTime: utcTime);

        var record = Normalizer.Normalize(raw, "ticketmaster", Artist, PassAllFilter, Now);

        record.Should().NotBeNull();
        record!.EventDateTime.Offset.Should().Be(TimeSpan.Zero,
            because: "UTC (Zulu) offset must be preserved as zero");
        record.EventDateTime.Should().Be(utcTime);
    }

    // ── Country allowlist ─────────────────────────────────────────────────────

    [Fact]
    public void Normalize_AppliesCountryAllowlist_WhenNotEmpty()
    {
        var filter = PassAllFilter with
        {
            CountryAllowlist = new HashSet<string> { "US", "GB" },
        };

        // Country not in allowlist.
        var rawDe = ValidRaw(country: "DE");
        var result = Normalizer.Normalize(rawDe, "ticketmaster", Artist, filter, Now);

        result.Should().BeNull(because: "DE is not in the allowlist {US, GB}");

        // Country in allowlist.
        var rawUs = ValidRaw(country: "US");
        var resultUs = Normalizer.Normalize(rawUs, "ticketmaster", Artist, filter, Now);
        resultUs.Should().NotBeNull(because: "US is in the allowlist");
    }

    [Fact]
    public void Normalize_AppliesCountryAllowlist_CaseInsensitive()
    {
        var filter = PassAllFilter with
        {
            CountryAllowlist = new HashSet<string> { "us" },  // lowercase
        };

        // Source returns uppercase "US"
        var raw = ValidRaw(country: "US");
        var result = Normalizer.Normalize(raw, "ticketmaster", Artist, filter, Now);

        result.Should().NotBeNull(because: "country matching must be case-insensitive");
    }

    // ── Date window ───────────────────────────────────────────────────────────

    [Fact]
    public void Normalize_AppliesMinMaxDaysAhead()
    {
        var minDate = Now.AddDays(7);
        var maxDate = Now.AddDays(30);

        var filter = PassAllFilter with
        {
            MinDate = minDate,
            MaxDate = maxDate,
        };

        // Too early
        var rawEarly = ValidRaw(eventDateTime: Now.AddDays(1));
        Normalizer.Normalize(rawEarly, "ticketmaster", Artist, filter, Now)
            .Should().BeNull(because: "event before MinDate must be filtered out");

        // Too late
        var rawLate = ValidRaw(eventDateTime: Now.AddDays(60));
        Normalizer.Normalize(rawLate, "ticketmaster", Artist, filter, Now)
            .Should().BeNull(because: "event after MaxDate must be filtered out");

        // In range
        var rawInRange = ValidRaw(eventDateTime: Now.AddDays(15));
        Normalizer.Normalize(rawInRange, "ticketmaster", Artist, filter, Now)
            .Should().NotBeNull(because: "event within [MinDate, MaxDate] must pass through");
    }

    // ── Festival / sold-out filters ───────────────────────────────────────────

    [Fact]
    public void Normalize_AppliesSkipFestivals()
    {
        var filter = PassAllFilter with { SkipFestivals = true };

        var rawFestival = ValidRaw(isFestival: true);
        Normalizer.Normalize(rawFestival, "ticketmaster", Artist, filter, Now)
            .Should().BeNull(because: "festivals must be filtered when SkipFestivals=true");

        var rawNormal = ValidRaw(isFestival: false);
        Normalizer.Normalize(rawNormal, "ticketmaster", Artist, filter, Now)
            .Should().NotBeNull(because: "non-festival events must pass when SkipFestivals=true");
    }

    [Fact]
    public void Normalize_AppliesSkipSoldOut()
    {
        var filter = PassAllFilter with { SkipSoldOut = true };

        var rawSoldOut = ValidRaw(isSoldOut: true);
        Normalizer.Normalize(rawSoldOut, "ticketmaster", Artist, filter, Now)
            .Should().BeNull(because: "sold-out events must be filtered when SkipSoldOut=true");

        var rawAvailable = ValidRaw(isSoldOut: false);
        Normalizer.Normalize(rawAvailable, "ticketmaster", Artist, filter, Now)
            .Should().NotBeNull(because: "available events must pass when SkipSoldOut=true");
    }

    // ── Lineup ordering ───────────────────────────────────────────────────────

    [Fact]
    public void Normalize_PreservesLineupOrder()
    {
        var lineup = new List<string> { "Radiohead", "Portishead", "Thom Yorke Solo" };
        var raw = ValidRaw(lineup: lineup);

        var record = Normalizer.Normalize(raw, "ticketmaster", Artist, PassAllFilter, Now);

        record.Should().NotBeNull();
        // ContainInConsecutiveOrder(IEnumerable<T>, string because, ...) overload.
        record!.Lineup.Should().ContainInConsecutiveOrder(
            new[] { "Radiohead", "Portishead", "Thom Yorke Solo" },
            because: "lineup order must be preserved from the raw event");
    }

    // ── Stable ID derivation ──────────────────────────────────────────────────

    [Fact]
    public void Normalize_GeneratesStableIdFromSourcePlusEventId()
    {
        var raw1 = ValidRaw(sourceEventId: "evt-001");
        var raw2 = ValidRaw(sourceEventId: "evt-001");
        var rawDiff = ValidRaw(sourceEventId: "evt-002");

        var record1 = Normalizer.Normalize(raw1, "ticketmaster", Artist, PassAllFilter, Now);
        var record2 = Normalizer.Normalize(raw2, "ticketmaster", Artist, PassAllFilter, Now);
        var recordDiff = Normalizer.Normalize(rawDiff, "ticketmaster", Artist, PassAllFilter, Now);

        record1.Should().NotBeNull();
        record2.Should().NotBeNull();
        recordDiff.Should().NotBeNull();

        record1!.Id.Should().Be(record2!.Id,
            because: "same source + source_event_id must always produce the same stable ID");

        record1.Id.Should().NotBe(recordDiff!.Id,
            because: "different source_event_id must produce a different stable ID");
    }
}
