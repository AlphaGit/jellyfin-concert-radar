using System;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Jellyfin.Plugin.ConcertRadar.Api;
using Jellyfin.Plugin.ConcertRadar.Configuration;
using Jellyfin.Plugin.ConcertRadar.Model;
using Jellyfin.Plugin.ConcertRadar.ScheduledTasks;
using Jellyfin.Plugin.ConcertRadar.Tests.Support;
using MediaBrowser.Common.Api;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.ConcertRadar.Tests.Api;

/// <summary>
/// Unit tests for <see cref="AdminController"/>.
/// The controller is instantiated directly with substitute repos and <see cref="ITaskManager"/>.
/// Authorization attributes are verified via reflection only — the ASP.NET pipeline enforces them.
/// </summary>
public sealed class AdminControllerTests : IAsyncLifetime
{
    private TestDatabase _db = null!;

    public async Task InitializeAsync()
        => _db = await TestDatabase.CreateAsync();

    public async Task DisposeAsync()
        => await _db.DisposeAsync();

    // ── Helpers ───────────────────────────────────────────────────────────────

    private AdminController BuildController(ITaskManager? taskManager = null)
        => new AdminController(
            _db.Concerts,
            _db.Artists,
            _db.SourceState,
            taskManager ?? Substitute.For<ITaskManager>(),
            Substitute.For<IHttpClientFactory>(),
            new StubPluginConfigurationProvider(new PluginConfiguration()),
            NullLogger<AdminController>.Instance);

    // ── Tests ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// AdminController carries [Authorize(Policy = Policies.RequiresElevation)] at the class level.
    /// </summary>
    [Fact]
    public void AdminController_HasRequiresElevationAttribute()
    {
        var attrs = typeof(AdminController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>()
            .ToList();

        attrs.Should().NotBeEmpty(
            "AdminController must require elevation via [Authorize(...)]");

        attrs.Should().Contain(
            a => a.Policy == Policies.RequiresElevation,
            "at least one [Authorize] attribute must specify the RequiresElevation policy");
    }

    /// <summary>
    /// RunNow queues RefreshConcertsTask exactly once and returns 202 Accepted.
    /// </summary>
    [Fact]
    public void AdminController_RunNow_QueuesTask()
    {
        var taskManager = Substitute.For<ITaskManager>();
        var controller  = BuildController(taskManager);

        var result = controller.RunNow();

        taskManager.Received(1).QueueScheduledTask<RefreshConcertsTask>();
        result.Should().BeOfType<AcceptedResult>(
            "RunNow must return 202 Accepted on success");
    }

    /// <summary>
    /// ResetSourceAsync clears the circuit breaker for the given source and returns 204.
    /// </summary>
    [Fact]
    public async Task AdminController_ResetSource_ClearsCircuit()
    {
        // Pre-open the circuit so there's state to clear.
        await _db.SourceState.RecordFailureAsync(
            "ticketmaster",
            "test error",
            threshold: 1,
            cooldown: TimeSpan.FromHours(24),
            now: DateTimeOffset.UtcNow,
            CancellationToken.None);

        bool openBefore = await _db.SourceState.IsOpenAsync(
            "ticketmaster", DateTimeOffset.UtcNow, CancellationToken.None);

        openBefore.Should().BeTrue("circuit is open before reset");

        var controller = BuildController();
        var result = await controller.ResetSourceAsync("ticketmaster", CancellationToken.None);

        result.Should().BeOfType<NoContentResult>("ResetSource must return 204 No Content");

        bool openAfter = await _db.SourceState.IsOpenAsync(
            "ticketmaster", DateTimeOffset.UtcNow, CancellationToken.None);

        openAfter.Should().BeFalse("circuit must be closed after reset");
    }

    /// <summary>
    /// PurgeAllAsync deletes all concerts and returns the count in the response.
    /// </summary>
    [Fact]
    public async Task AdminController_Purge_EmptiesRepo()
    {
        // Seed some concerts.
        await _db.Concerts.UpsertAsync(
            ConcertRecordFactory.Create(sourceEventId: "purge-evt-1"),
            CancellationToken.None);
        await _db.Concerts.UpsertAsync(
            ConcertRecordFactory.Create(sourceEventId: "purge-evt-2"),
            CancellationToken.None);

        var controller = BuildController();
        var result = await controller.PurgeAllAsync(CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var purgeResult = ok.Value.Should().BeOfType<AdminController.PurgeResult>().Subject;
        purgeResult.Deleted.Should().Be(2, "two concerts were seeded");

        // Verify table is empty.
        var remaining = await _db.Concerts.QueryAsync(
            new ConcertQuery(),
            CancellationToken.None);

        remaining.Total.Should().Be(0, "PurgeAll must delete all rows");
    }

    /// <summary>
    /// ResolveIdsAsync clears all ext_ids and returns 204.
    /// </summary>
    [Fact]
    public async Task AdminController_ResolveIds_ClearsExtIds()
    {
        // Seed an artist with an ext_id.
        await _db.Artists.UpsertFromLibraryAsync(
            new[] { new LibraryArtist(Guid.NewGuid(), "Radiohead", "a74b1b7f-71a5-4011-9441-d0b5e4122711") },
            CancellationToken.None);

        string artistId = Jellyfin.Plugin.ConcertRadar.Storage.ArtistRepository
            .ComputeId("a74b1b7f-71a5-4011-9441-d0b5e4122711", "Radiohead");

        await _db.Artists.SetExternalIdsAsync(
            artistId,
            new System.Collections.Generic.Dictionary<string, string>
            {
                ["songkick"] = "253846",
            },
            CancellationToken.None);

        var controller = BuildController();
        var result = await controller.ResolveIdsAsync(CancellationToken.None);

        result.Should().BeOfType<NoContentResult>("ResolveIds must return 204 No Content");

        // Verify ext_ids were cleared.
        var stored = await _db.Artists.GetNextBatchAsync(10, CancellationToken.None);
        stored.Should().ContainSingle()
            .Which.ExternalIds.Should().BeEmpty("all ext_ids must be cleared");
    }
}
