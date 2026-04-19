using System;

namespace Jellyfin.Plugin.ConcertRadar.Sources;

/// <summary>
/// Validates that a scraper-supplied URL belongs to the expected host before it is stored
/// in the database as a <c>source_url</c>. Prevents open-redirect via a compromised upstream.
/// </summary>
internal static class UrlGuard
{
    /// <summary>
    /// Returns <c>true</c> when <paramref name="url"/> is an absolute HTTPS URL whose host
    /// is <paramref name="expectedHost"/> or a direct subdomain of it (e.g. <c>tickets.dice.fm</c>).
    /// </summary>
    public static bool IsHostAllowed(string? url, string expectedHost)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        string host = uri.Host;
        return host.Equals(expectedHost, StringComparison.OrdinalIgnoreCase)
            || host.EndsWith("." + expectedHost, StringComparison.OrdinalIgnoreCase);
    }
}
