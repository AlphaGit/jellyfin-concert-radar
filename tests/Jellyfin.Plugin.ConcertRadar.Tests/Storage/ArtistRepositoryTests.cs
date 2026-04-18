using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Jellyfin.Plugin.ConcertRadar.Model;
using Jellyfin.Plugin.ConcertRadar.Storage;
using Jellyfin.Plugin.ConcertRadar.Tests.Support;
using Xunit;

namespace Jellyfin.Plugin.ConcertRadar.Tests.Storage;

/// <summary>
/// Integration tests for <see cref="ArtistRepository"/>.
/// </summary>
public class ArtistRepositoryTests
{
    private static readonly DateTimeOffset Now =
        new DateTimeOffset(2025, 6, 15, 12, 0, 0, TimeSpan.Zero);

    private static LibraryArtist MakeArtist(string name, string? mbid = null, Guid? id = null)
        => new LibraryArtist(id ?? Guid.NewGuid(), name, mbid);

    // ── UpsertFromLibrary ─────────────────────────────────────────────────────

    [Fact]
    public async Task UpsertFromLibrary_InsertsNewArtists()
    {
        await using var db = await TestDatabase.CreateAsync();

        var artists = new[]
        {
            MakeArtist("Radiohead", "a74b1b7f-71a5-4011-9441-d0b5e4122711"),
            MakeArtist("Portishead"),
        };

        await db.Artists.UpsertFromLibraryAsync(artists, CancellationToken.None);

        var batch = await db.Artists.GetNextBatchAsync(50, CancellationToken.None);
        batch.Should().HaveCount(2);
        batch.Select(a => a.Name).Should().BeEquivalentTo(new[] { "Radiohead", "Portishead" });
    }

    [Fact]
    public async Task UpsertFromLibrary_UpdatesNameAndMbid_DoesNotDelete()
    {
        await using var db = await TestDatabase.CreateAsync();

        // Artist already has an MBID — PK is the MBID, so the same row is updated on re-upsert.
        const string mbid = "a74b1b7f-71a5-4011-9441-d0b5e4122711";
        var jellyfinId = Guid.NewGuid();

        await db.Artists.UpsertFromLibraryAsync(
            new[] { MakeArtist("Old Name", mbid: mbid, id: jellyfinId) },
            CancellationToken.None);

        // Re-upsert with updated name — same MBID so same PK → UPDATE path.
        await db.Artists.UpsertFromLibraryAsync(
            new[] { MakeArtist("New Name", mbid: mbid, id: jellyfinId) },
            CancellationToken.None);

        var batch = await db.Artists.GetNextBatchAsync(50, CancellationToken.None);

        // Only one row — not duplicated.
        var artist = batch.Should().ContainSingle().Subject;
        artist.Name.Should().Be("New Name", because: "name is updated on conflict");
        artist.Mbid.Should().Be(mbid, because: "MBID is preserved on conflict");
    }

    [Fact]
    public async Task UpsertFromLibrary_DoesNotDeleteArtistsAbsentFromInput()
    {
        await using var db = await TestDatabase.CreateAsync();

        await db.Artists.UpsertFromLibraryAsync(
            new[] { MakeArtist("Artist A"), MakeArtist("Artist B") },
            CancellationToken.None);

        // Re-run with only Artist A — Artist B should still be present (soft-missing).
        await db.Artists.UpsertFromLibraryAsync(
            new[] { MakeArtist("Artist A") },
            CancellationToken.None);

        var batch = await db.Artists.GetNextBatchAsync(50, CancellationToken.None);
        batch.Should().HaveCount(2, because: "UpsertFromLibrary never deletes existing artists");
    }

    // ── GetNextBatch ──────────────────────────────────────────────────────────

    [Fact]
    public async Task GetNextBatch_ReturnsUpToLimit_OldestFirst()
    {
        await using var db = await TestDatabase.CreateAsync();

        // Insert 5 artists and mark them checked in reverse order so we know the expected sort.
        var names = new[] { "A", "B", "C", "D", "E" };
        var items = names.Select(n => MakeArtist(n)).ToArray();
        await db.Artists.UpsertFromLibraryAsync(items, CancellationToken.None);

        // Compute IDs to update timestamps.
        // Mark them checked from oldest (A) to newest (E).
        for (int i = 0; i < names.Length; i++)
        {
            string artistId = ArtistRepository.ComputeId(null, names[i]);
            await db.Artists.UpdateCheckedAsync(artistId, Now.AddHours(i), null, CancellationToken.None);
        }

        // Request only 3 — should get A, B, C (oldest timestamps first).
        var batch = await db.Artists.GetNextBatchAsync(3, CancellationToken.None);

        batch.Should().HaveCount(3);
        batch.Select(a => a.Name).Should()
            .Equal(new[] { "A", "B", "C" }, "GetNextBatch orders by last_checked_at ASC");
    }

    [Fact]
    public async Task GetNextBatch_NewArtistsJumpToFront()
    {
        await using var db = await TestDatabase.CreateAsync();

        var oldArtist = MakeArtist("Old Artist");
        var newArtist = MakeArtist("New Artist"); // last_checked_at will be NULL

        await db.Artists.UpsertFromLibraryAsync(new[] { oldArtist }, CancellationToken.None);

        // Mark old artist as checked.
        string oldId = ArtistRepository.ComputeId(null, "Old Artist");
        await db.Artists.UpdateCheckedAsync(oldId, Now, null, CancellationToken.None);

        // Insert the new unchecked artist.
        await db.Artists.UpsertFromLibraryAsync(new[] { newArtist }, CancellationToken.None);

        var batch = await db.Artists.GetNextBatchAsync(2, CancellationToken.None);

        batch.Should().HaveCount(2);
        batch.First().Name.Should().Be("New Artist",
            because: "NULLS FIRST means unchecked artists come before checked ones");
    }

    // ── UpdateChecked ─────────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateChecked_SetsTimestampAndClearsError()
    {
        await using var db = await TestDatabase.CreateAsync();

        var artist = MakeArtist("Test Artist");
        await db.Artists.UpsertFromLibraryAsync(new[] { artist }, CancellationToken.None);
        string id = ArtistRepository.ComputeId(null, "Test Artist");

        // Simulate a previous error.
        await db.Artists.UpdateCheckedAsync(id, Now.AddHours(-1), "some error", CancellationToken.None);

        // Then a success.
        await db.Artists.UpdateCheckedAsync(id, Now, null, CancellationToken.None);

        var batch = await db.Artists.GetNextBatchAsync(1, CancellationToken.None);
        var stored = batch.Single();

        stored.LastCheckedAt.Should().BeCloseTo(Now, precision: TimeSpan.FromSeconds(1));
        stored.LastError.Should().BeNull(because: "null error clears last_error");
        stored.ConsecutiveErrors.Should().Be(0, because: "null error resets consecutive_errors to 0");
    }

    [Fact]
    public async Task UpdateChecked_IncrementsConsecutiveErrors_OnError()
    {
        await using var db = await TestDatabase.CreateAsync();

        var artist = MakeArtist("Error Artist");
        await db.Artists.UpsertFromLibraryAsync(new[] { artist }, CancellationToken.None);
        string id = ArtistRepository.ComputeId(null, "Error Artist");

        await db.Artists.UpdateCheckedAsync(id, Now.AddHours(-2), "error #1", CancellationToken.None);
        await db.Artists.UpdateCheckedAsync(id, Now.AddHours(-1), "error #2", CancellationToken.None);
        await db.Artists.UpdateCheckedAsync(id, Now,              "error #3", CancellationToken.None);

        var batch = await db.Artists.GetNextBatchAsync(1, CancellationToken.None);
        var stored = batch.Single();

        stored.ConsecutiveErrors.Should().Be(3, because: "each error call increments consecutive_errors");
        stored.LastError.Should().Be("error #3");
    }

    // ── SetExternalIds ────────────────────────────────────────────────────────

    [Fact]
    public async Task SetExternalIds_MergesWithExisting()
    {
        await using var db = await TestDatabase.CreateAsync();

        var artist = MakeArtist("Merge Artist");
        await db.Artists.UpsertFromLibraryAsync(new[] { artist }, CancellationToken.None);
        string id = ArtistRepository.ComputeId(null, "Merge Artist");

        // Write first set of IDs.
        await db.Artists.SetExternalIdsAsync(id,
            new Dictionary<string, string> { ["songkick"] = "12345" },
            CancellationToken.None);

        // Merge second set — should not overwrite songkick.
        await db.Artists.SetExternalIdsAsync(id,
            new Dictionary<string, string> { ["bandsintown"] = "67890" },
            CancellationToken.None);

        var batch = await db.Artists.GetNextBatchAsync(1, CancellationToken.None);
        var stored = batch.Single();

        stored.ExternalIds.Should().ContainKey("songkick").WhoseValue.Should().Be("12345",
            because: "SetExternalIds merges, not replaces");
        stored.ExternalIds.Should().ContainKey("bandsintown").WhoseValue.Should().Be("67890");
    }

    // ── ResetAllChecked ───────────────────────────────────────────────────────

    [Fact]
    public async Task ResetAllChecked_NullifiesTimestamps()
    {
        await using var db = await TestDatabase.CreateAsync();

        var artists = new[] { MakeArtist("X"), MakeArtist("Y"), MakeArtist("Z") };
        await db.Artists.UpsertFromLibraryAsync(artists, CancellationToken.None);

        // Mark all as checked.
        foreach (var a in artists)
        {
            string aid = ArtistRepository.ComputeId(null, a.Name);
            await db.Artists.UpdateCheckedAsync(aid, Now, null, CancellationToken.None);
        }

        // Verify they have timestamps before reset.
        var beforeReset = await db.Artists.GetNextBatchAsync(50, CancellationToken.None);
        beforeReset.Should().OnlyContain(a => a.LastCheckedAt.HasValue,
            because: "all artists should be checked before reset");

        await db.Artists.ResetAllCheckedAsync(CancellationToken.None);

        var afterReset = await db.Artists.GetNextBatchAsync(50, CancellationToken.None);
        afterReset.Should().OnlyContain(a => !a.LastCheckedAt.HasValue,
            because: "ResetAllChecked must nullify last_checked_at for every artist");
    }
}
