using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Jellyfin.Plugin.ConcertRadar.Api;
using Jellyfin.Plugin.ConcertRadar.Api.Dtos;
using Jellyfin.Plugin.ConcertRadar.Model;
using Jellyfin.Plugin.ConcertRadar.Tests.Support;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.ConcertRadar.Tests.Api;

/// <summary>
/// Unit tests for <see cref="ConcertsController"/>.
/// The controller is instantiated directly with substitute repositories and ITaskManager.
/// No WebApplicationFactory — authorization attributes are verified via reflection only.
/// </summary>
public sealed class ConcertsControllerTests : IAsyncLifetime
{
    private TestDatabase _db = null!;

    public async Task InitializeAsync()
        => _db = await TestDatabase.CreateAsync();

    public async Task DisposeAsync()
        => await _db.DisposeAsync();

    // ── Helpers ───────────────────────────────────────────────────────────────

    private ConcertsController BuildController()
        => new ConcertsController(
            _db.Concerts,
            _db.Artists,
            _db.SourceState,
            Substitute.For<ITaskManager>(),
            NullLogger<ConcertsController>.Instance);

    // ── Tests ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// ConcertsController is decorated with [Authorize] at the class level.
    /// </summary>
    [Fact]
    public void ConcertsController_HasAuthorizeAttribute()
    {
        var attrs = typeof(ConcertsController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true);

        attrs.Should().NotBeEmpty(
            "ConcertsController must be protected by [Authorize]");
    }

    /// <summary>
    /// GetConcertsAsync returns OK with concerts matching the seeded data.
    /// </summary>
    [Fact]
    public async Task ConcertsController_GetConcerts_ReturnsExpectedDtos()
    {
        var record = ConcertRecordFactory.Create(
            source: "ticketmaster",
            sourceEventId: "tm-test-001",
            artistName: "Radiohead",
            city: "London");

        await _db.Concerts.UpsertAsync(record, CancellationToken.None);

        var controller = BuildController();
        var result = await controller.GetConcertsAsync(cancellationToken: CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var qr = ok.Value.Should().BeOfType<QueryResult<ConcertDto>>().Subject;
        qr.Total.Should().Be(1);
        qr.Items.Should().ContainSingle(c =>
            c.Source == "ticketmaster" && c.ArtistName == "Radiohead");
    }

    /// <summary>
    /// Pagination: page 2 of pageSize=1 returns the second concert.
    /// </summary>
    [Fact]
    public async Task ConcertsController_GetConcerts_PaginatesAndSorts()
    {
        // Insert two concerts: same artist, different event IDs, ordered by date.
        var earlier = ConcertRecordFactory.Create(
            source: "ticketmaster",
            sourceEventId: "tm-pg-001",
            artistName: "Radiohead",
            eventDateTime: new DateTimeOffset(2025, 9, 1, 20, 0, 0, TimeSpan.Zero));

        var later = ConcertRecordFactory.Create(
            source: "ticketmaster",
            sourceEventId: "tm-pg-002",
            artistName: "Radiohead",
            eventDateTime: new DateTimeOffset(2025, 10, 1, 20, 0, 0, TimeSpan.Zero));

        await _db.Concerts.UpsertAsync(earlier, CancellationToken.None);
        await _db.Concerts.UpsertAsync(later, CancellationToken.None);

        var controller = BuildController();

        // Page 1 (page param=2, converted to 0-based inside controller as page=1)
        var result = await controller.GetConcertsAsync(
            page: 2,
            pageSize: 1,
            sort: "date",
            cancellationToken: CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var qr = ok.Value.Should().BeOfType<QueryResult<ConcertDto>>().Subject;
        qr.Total.Should().Be(2, "total reflects all matching rows");
        qr.Items.Should().ContainSingle()
            .Which.SourceEventId.Should().Be("tm-pg-002",
                "second page returns the later event when sorted by date");
    }

    /// <summary>
    /// GetArtistsAsync returns all artists from the database.
    /// </summary>
    [Fact]
    public async Task ConcertsController_GetArtists_ReturnsExpectedDtos()
    {
        await _db.Artists.UpsertFromLibraryAsync(
            new[]
            {
                new LibraryArtist(Guid.NewGuid(), "Radiohead", "a74b1b7f-71a5-4011-9441-d0b5e4122711"),
                new LibraryArtist(Guid.NewGuid(), "Portishead", null),
            },
            CancellationToken.None);

        var controller = BuildController();
        var result = await controller.GetArtistsAsync(CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var qr = ok.Value.Should().BeOfType<QueryResult<ArtistDto>>().Subject;
        qr.Items.Should().HaveCount(2);
        qr.Items.Should().Contain(a => a.Name == "Radiohead" && a.Mbid == "a74b1b7f-71a5-4011-9441-d0b5e4122711");
        qr.Items.Should().Contain(a => a.Name == "Portishead" && a.Mbid == null);
    }

    /// <summary>
    /// GetStatusAsync returns source cards and queue size.
    /// </summary>
    [Fact]
    public async Task ConcertsController_GetStatus_ReturnsSourceCardsAndQueueSize()
    {
        // Insert an artist without last_checked_at to populate the queue.
        await _db.Artists.UpsertFromLibraryAsync(
            new[] { new LibraryArtist(Guid.NewGuid(), "Unchecked Artist", null) },
            CancellationToken.None);

        var taskManager = Substitute.For<ITaskManager>();
        taskManager.ScheduledTasks.Returns(Array.Empty<IScheduledTaskWorker>());

        var controller = new ConcertsController(
            _db.Concerts,
            _db.Artists,
            _db.SourceState,
            taskManager,
            NullLogger<ConcertsController>.Instance);

        var result = await controller.GetStatusAsync(CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var statusResp = ok.Value.Should().BeAssignableTo<ConcertsController.StatusResponse>().Subject;
        statusResp.QueueSize.Should().Be(1,
            "one artist has never been checked → queue size = 1");
        statusResp.Sources.Should().NotBeNull();
    }
}
