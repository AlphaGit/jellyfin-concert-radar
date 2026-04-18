using System;
using System.Collections.Generic;
using Jellyfin.Plugin.ConcertRadar.Configuration;

namespace Jellyfin.Plugin.ConcertRadar.Model;

/// <summary>
/// Immutable filter bag derived from <see cref="PluginConfiguration"/> and passed to each
/// <c>ISourceAdapter</c> and the <see cref="Normalization.Normalizer"/> for post-fetch filtering.
/// </summary>
/// <param name="Locations">Geographic location filters (city/region/country/lat/lon/radius).</param>
/// <param name="CountryAllowlist">ISO-3166 alpha-2 codes to include; empty means all countries.</param>
/// <param name="GenreAllowlist">Genre names to include; empty means all genres.</param>
/// <param name="MinDate">Earliest event date to include.</param>
/// <param name="MaxDate">Latest event date to include.</param>
/// <param name="SkipFestivals">When <c>true</c>, festival events are excluded.</param>
/// <param name="SkipSoldOut">When <c>true</c>, sold-out events are excluded.</param>
public sealed record SourceFilter(
    IReadOnlyList<LocationFilter> Locations,
    IReadOnlySet<string> CountryAllowlist,
    IReadOnlySet<string> GenreAllowlist,
    DateTimeOffset MinDate,
    DateTimeOffset MaxDate,
    bool SkipFestivals,
    bool SkipSoldOut);
