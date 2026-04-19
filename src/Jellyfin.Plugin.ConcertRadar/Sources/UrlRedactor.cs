using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.ConcertRadar.Sources;

/// <summary>
/// Utility for removing credential query parameters from URLs before they appear in log
/// messages or exception strings.  Prevents API keys from leaking into <c>source_state.last_error</c>
/// or HTTP server logs.
/// </summary>
internal static partial class UrlRedactor
{
    // Matches credential-bearing query parameters and replaces their values with ***.
    // Covers: apikey, api_key, app_id, client, key, token (common patterns across adapters).
    [GeneratedRegex(
        @"(?<=(?:^|[?&])(?:apikey|api_key|app_id|client|key|token)=)[^&\s#]+",
        RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 500)]
    private static partial Regex CredentialValuePattern();

    /// <summary>
    /// Returns <paramref name="url"/> with any credential query-parameter values replaced
    /// by <c>***</c>.  Safe to call with <c>null</c>; returns an empty string in that case.
    /// </summary>
    public static string Redact(string? url)
    {
        if (string.IsNullOrEmpty(url))
            return string.Empty;

        return CredentialValuePattern().Replace(url, "***");
    }
}
