using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.ConcertRadar.Library;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.ConcertRadar.Tests.Library;

/// <summary>
/// Unit tests for <see cref="LibraryArtistEnumerator"/>.
/// Uses NSubstitute to fake <see cref="ILibraryManager"/> with no IO.
/// </summary>
public class LibraryArtistEnumeratorTests
{
    // ── Helper ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a <see cref="MusicArtist"/> with the given name and optional provider ID.
    /// <see cref="MusicArtist"/> is a concrete non-sealed class with a public parameterless ctor,
    /// so it can be constructed directly.
    /// </summary>
    private static MusicArtist MakeArtist(string name, string? mbid = null)
    {
        var artist = new MusicArtist
        {
            Id   = Guid.NewGuid(),
            Name = name,
        };

        if (mbid != null)
            artist.ProviderIds["MusicBrainzArtist"] = mbid;

        return artist;
    }

    private static LibraryArtistEnumerator BuildEnumerator(IReadOnlyList<MusicArtist> artists)
    {
        var library = Substitute.For<ILibraryManager>();

        // Match the InternalItemsQuery that LibraryArtistEnumerator constructs.
        library.GetItemList(Arg.Any<InternalItemsQuery>())
               .Returns(artists);

        return new LibraryArtistEnumerator(library, NullLogger<LibraryArtistEnumerator>.Instance);
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Enumerate_YieldsMbidFromProviderIds()
    {
        const string mbid = "a74b1b7f-71a5-4011-9441-d0b5e4122711";
        var artists = new[] { MakeArtist("Radiohead", mbid) };
        var enumerator = BuildEnumerator(artists);

        var result = enumerator.Enumerate().ToList();

        result.Should().ContainSingle();
        result[0].Name.Should().Be("Radiohead");
        result[0].Mbid.Should().Be(mbid,
            because: "MBID must be extracted from the MusicBrainzArtist provider ID");
    }

    [Fact]
    public void Enumerate_SplitsCommaSeparatedMbids_TakesFirst()
    {
        const string first  = "a74b1b7f-71a5-4011-9441-d0b5e4122711";
        const string second = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";
        var artists = new[] { MakeArtist("Radiohead", $"{first},{second}") };
        var enumerator = BuildEnumerator(artists);

        var result = enumerator.Enumerate().ToList();

        result.Should().ContainSingle();
        result[0].Mbid.Should().Be(first,
            because: "when multiple MBIDs are comma-separated, only the first is used");
    }

    [Fact]
    public void Enumerate_YieldsNullMbidWhenAbsent()
    {
        var artists = new[] { MakeArtist("UnknownBand") };
        var enumerator = BuildEnumerator(artists);

        var result = enumerator.Enumerate().ToList();

        result.Should().ContainSingle();
        result[0].Name.Should().Be("UnknownBand");
        result[0].Mbid.Should().BeNull(
            because: "artists without a MusicBrainzArtist provider ID must yield a null MBID");
    }

    [Fact]
    public void Enumerate_YieldsAllArtists_IncludingMixed()
    {
        var artists = new[]
        {
            MakeArtist("Radiohead", "a74b1b7f-71a5-4011-9441-d0b5e4122711"),
            MakeArtist("Portishead"),
            MakeArtist("Thom Yorke", "55a6b5a0-6bf1-4a78-b980-8d4f4b9e0f8e"),
        };
        var enumerator = BuildEnumerator(artists);

        var result = enumerator.Enumerate().ToList();

        result.Should().HaveCount(3);
        result.Count(a => a.Mbid != null).Should().Be(2,
            because: "two of the three artists have MusicBrainz IDs");
        result.Count(a => a.Mbid == null).Should().Be(1,
            because: "one artist has no MBID");
    }
}
