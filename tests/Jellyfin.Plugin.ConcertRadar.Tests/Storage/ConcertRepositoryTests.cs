using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Jellyfin.Plugin.ConcertRadar.Model;
using Jellyfin.Plugin.ConcertRadar.Tests.Support;
using Xunit;

namespace Jellyfin.Plugin.ConcertRadar.Tests.Storage;

/// <summary>
/// Integration tests for <see cref="Jellyfin.Plugin.ConcertRadar.Storage.ConcertRepository"/>.
/// </summary>
public class ConcertRepositoryTests
{
    private static readonly DateTimeOffset Now =
        new DateTimeOffset(2025, 6, 15, 12, 0, 0, TimeSpan.Zero);

    // ── Upsert ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Upsert_InsertsNewRecord()
    {
        await using var db = await TestDatabase.CreateAsync();
        var record = ConcertRecordFactory.Create();

        await db.Concerts.UpsertAsync(record, CancellationToken.None);

        var result = await db.Concerts.QueryAsync(new ConcertQuery(), CancellationToken.None);
        result.Total.Should().Be(1);
        result.Items.Should().ContainSingle(r => r.Id == record.Id);
    }

    [Fact]
    public async Task Upsert_UpdatesExistingBySourceAndSourceEventId()
    {
        await using var db = await TestDatabase.CreateAsync();
        var original = ConcertRecordFactory.Create(
            id: Guid.NewGuid().ToString(),
            source: "ticketmaster",
            sourceEventId: "evt-shared",
            artistName: "Old Name");

        await db.Concerts.UpsertAsync(original, CancellationToken.None);

        // Same (source, source_event_id) but different content.
        var updated = ConcertRecordFactory.Create(
            id: Guid.NewGuid().ToString(), // different PK — conflict resolved by UNIQUE(source, source_event_id)
            source: "ticketmaster",
            sourceEventId: "evt-shared",
            artistName: "New Name");

        await db.Concerts.UpsertAsync(updated, CancellationToken.None);

        var result = await db.Concerts.QueryAsync(new ConcertQuery(), CancellationToken.None);
        result.Total.Should().Be(1, because: "upsert must not create a duplicate row");
        result.Items.Single().ArtistName.Should().Be("New Name",
            because: "upsert must overwrite the artist_name on conflict");
    }

    [Fact]
    public async Task Upsert_DoesNotDuplicateAcrossSources()
    {
        await using var db = await TestDatabase.CreateAsync();

        // Same source_event_id but different sources → two distinct rows.
        var r1 = ConcertRecordFactory.Create(source: "ticketmaster", sourceEventId: "common-id");
        var r2 = ConcertRecordFactory.Create(source: "bandsintown",  sourceEventId: "common-id");

        await db.Concerts.UpsertAsync(r1, CancellationToken.None);
        await db.Concerts.UpsertAsync(r2, CancellationToken.None);

        var result = await db.Concerts.QueryAsync(new ConcertQuery(), CancellationToken.None);
        result.Total.Should().Be(2, because: "(source, source_event_id) is the unique key, not source_event_id alone");
    }

    // ── Query filters ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Query_FiltersByDateRange()
    {
        await using var db = await TestDatabase.CreateAsync();

        var future  = ConcertRecordFactory.Create(sourceEventId: "future",  eventDateTime: Now.AddDays(10));
        var present = ConcertRecordFactory.Create(sourceEventId: "present", eventDateTime: Now.AddDays(1));
        var past    = ConcertRecordFactory.Create(sourceEventId: "past",    eventDateTime: Now.AddDays(-5));

        foreach (var r in new[] { future, present, past })
            await db.Concerts.UpsertAsync(r, CancellationToken.None);

        var result = await db.Concerts.QueryAsync(
            new ConcertQuery(From: Now, To: Now.AddDays(5)),
            CancellationToken.None);

        result.Total.Should().Be(1, because: "only the 'present' event falls within [Now, Now+5]");
        result.Items.Single().SourceEventId.Should().Be("present");
    }

    [Fact]
    public async Task Query_FiltersByCountry()
    {
        await using var db = await TestDatabase.CreateAsync();

        var us = ConcertRecordFactory.Create(sourceEventId: "us-event", country: "US");
        var gb = ConcertRecordFactory.Create(sourceEventId: "gb-event", country: "GB");

        await db.Concerts.UpsertAsync(us, CancellationToken.None);
        await db.Concerts.UpsertAsync(gb, CancellationToken.None);

        var result = await db.Concerts.QueryAsync(
            new ConcertQuery(Country: "GB"),
            CancellationToken.None);

        result.Total.Should().Be(1);
        result.Items.Single().Country.Should().Be("GB");
    }

    [Fact]
    public async Task Query_FiltersByCity()
    {
        await using var db = await TestDatabase.CreateAsync();

        var nyc    = ConcertRecordFactory.Create(sourceEventId: "nyc", city: "New York");
        var london = ConcertRecordFactory.Create(sourceEventId: "lon", city: "London");

        await db.Concerts.UpsertAsync(nyc, CancellationToken.None);
        await db.Concerts.UpsertAsync(london, CancellationToken.None);

        var result = await db.Concerts.QueryAsync(
            new ConcertQuery(City: "London"),
            CancellationToken.None);

        result.Total.Should().Be(1);
        result.Items.Single().City.Should().Be("London");
    }

    [Fact]
    public async Task Query_FiltersBySource()
    {
        await using var db = await TestDatabase.CreateAsync();

        var tm  = ConcertRecordFactory.Create(sourceEventId: "tm-1",  source: "ticketmaster");
        var bit = ConcertRecordFactory.Create(sourceEventId: "bit-1", source: "bandsintown");

        await db.Concerts.UpsertAsync(tm, CancellationToken.None);
        await db.Concerts.UpsertAsync(bit, CancellationToken.None);

        var result = await db.Concerts.QueryAsync(
            new ConcertQuery(Source: "bandsintown"),
            CancellationToken.None);

        result.Total.Should().Be(1);
        result.Items.Single().Source.Should().Be("bandsintown");
    }

    [Fact]
    public async Task Query_FiltersByArtistMbid()
    {
        await using var db = await TestDatabase.CreateAsync();

        const string mbid1 = "a74b1b7f-71a5-4011-9441-d0b5e4122711";
        const string mbid2 = "bfcc6d75-a6a5-4bc6-8282-47aec8531818";

        var r1 = ConcertRecordFactory.Create(sourceEventId: "r1", artistMbid: mbid1);
        var r2 = ConcertRecordFactory.Create(sourceEventId: "r2", artistMbid: mbid2);

        await db.Concerts.UpsertAsync(r1, CancellationToken.None);
        await db.Concerts.UpsertAsync(r2, CancellationToken.None);

        var result = await db.Concerts.QueryAsync(
            new ConcertQuery(ArtistMbid: mbid2),
            CancellationToken.None);

        result.Total.Should().Be(1);
        result.Items.Single().ArtistMbid.Should().Be(mbid2);
    }

    // ── Pagination ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Query_PaginatesCorrectly()
    {
        await using var db = await TestDatabase.CreateAsync();

        // Insert 5 records with distinct event times so ordering is deterministic.
        for (int i = 0; i < 5; i++)
        {
            var r = ConcertRecordFactory.Create(
                sourceEventId: $"evt-{i:D2}",
                eventDateTime: Now.AddDays(i));
            await db.Concerts.UpsertAsync(r, CancellationToken.None);
        }

        // Page 0, size 2.
        var page0 = await db.Concerts.QueryAsync(
            new ConcertQuery(Page: 0, PageSize: 2),
            CancellationToken.None);

        // Page 1, size 2.
        var page1 = await db.Concerts.QueryAsync(
            new ConcertQuery(Page: 1, PageSize: 2),
            CancellationToken.None);

        page0.Total.Should().Be(5, because: "total reflects all rows regardless of page");
        page0.Items.Should().HaveCount(2);
        page1.Items.Should().HaveCount(2);

        // Items across pages must not overlap.
        var page0Ids = page0.Items.Select(x => x.SourceEventId).ToHashSet();
        var page1Ids = page1.Items.Select(x => x.SourceEventId).ToHashSet();
        page0Ids.Intersect(page1Ids).Should().BeEmpty(because: "pages must not repeat the same records");
    }

    [Fact]
    public async Task Query_SortsByDate_AscDefault()
    {
        await using var db = await TestDatabase.CreateAsync();

        var r3 = ConcertRecordFactory.Create(sourceEventId: "third",  eventDateTime: Now.AddDays(3));
        var r1 = ConcertRecordFactory.Create(sourceEventId: "first",  eventDateTime: Now.AddDays(1));
        var r2 = ConcertRecordFactory.Create(sourceEventId: "second", eventDateTime: Now.AddDays(2));

        // Insert out of chronological order.
        await db.Concerts.UpsertAsync(r3, CancellationToken.None);
        await db.Concerts.UpsertAsync(r1, CancellationToken.None);
        await db.Concerts.UpsertAsync(r2, CancellationToken.None);

        var result = await db.Concerts.QueryAsync(
            new ConcertQuery(Sort: ConcertSortField.Date),
            CancellationToken.None);

        result.Items.Should().BeInAscendingOrder(r => r.EventDateTime,
            because: "default sort is ascending by event_datetime");
    }

    // ── GC operations ─────────────────────────────────────────────────────────

    [Fact]
    public async Task DeletePast_RemovesOnlyPastEvents()
    {
        await using var db = await TestDatabase.CreateAsync();

        var past   = ConcertRecordFactory.Create(sourceEventId: "past",   eventDateTime: Now.AddDays(-1));
        var future = ConcertRecordFactory.Create(sourceEventId: "future", eventDateTime: Now.AddDays(+1));

        await db.Concerts.UpsertAsync(past, CancellationToken.None);
        await db.Concerts.UpsertAsync(future, CancellationToken.None);

        int deleted = await db.Concerts.DeletePastAsync(Now, CancellationToken.None);

        deleted.Should().Be(1, because: "only the past event is before Now");

        var remaining = await db.Concerts.QueryAsync(new ConcertQuery(), CancellationToken.None);
        remaining.Total.Should().Be(1);
        remaining.Items.Single().SourceEventId.Should().Be("future");
    }

    [Fact]
    public async Task DeleteStale_RemovesOnlyBeyondThreshold()
    {
        await using var db = await TestDatabase.CreateAsync();

        var staleTime  = Now.AddDays(-20);
        var freshTime  = Now.AddDays(-1);

        var stale = ConcertRecordFactory.Create(sourceEventId: "stale", lastSeenAt: staleTime,  eventDateTime: Now.AddDays(30));
        var fresh = ConcertRecordFactory.Create(sourceEventId: "fresh", lastSeenAt: freshTime,   eventDateTime: Now.AddDays(30));

        await db.Concerts.UpsertAsync(stale, CancellationToken.None);
        await db.Concerts.UpsertAsync(fresh, CancellationToken.None);

        // Threshold: anything not seen for > 14 days is stale.
        var threshold = Now.AddDays(-14);
        int deleted = await db.Concerts.DeleteStaleAsync(threshold, CancellationToken.None);

        deleted.Should().Be(1, because: "only the record with last_seen_at 20 days ago is stale");

        var remaining = await db.Concerts.QueryAsync(new ConcertQuery(), CancellationToken.None);
        remaining.Total.Should().Be(1);
        remaining.Items.Single().SourceEventId.Should().Be("fresh");
    }

    [Fact]
    public async Task PurgeAll_EmptiesTable()
    {
        await using var db = await TestDatabase.CreateAsync();

        for (int i = 0; i < 3; i++)
            await db.Concerts.UpsertAsync(
                ConcertRecordFactory.Create(sourceEventId: $"evt-{i}"),
                CancellationToken.None);

        int deleted = await db.Concerts.PurgeAllAsync(CancellationToken.None);

        deleted.Should().Be(3);

        var result = await db.Concerts.QueryAsync(new ConcertQuery(), CancellationToken.None);
        result.Total.Should().Be(0, because: "PurgeAll must delete every row");
    }
}
