using System.Collections.Generic;

namespace Jellyfin.Plugin.ConcertRadar.Model;

/// <summary>
/// Paginated query result wrapping a list of items and the total count of matching rows.
/// </summary>
/// <typeparam name="T">Item type.</typeparam>
/// <param name="Items">The items on the requested page.</param>
/// <param name="Total">The total number of rows matching the query (before pagination).</param>
public sealed record QueryResult<T>(IReadOnlyList<T> Items, int Total);
