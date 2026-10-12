namespace Jellyfin.Plugin.ConcertRadar.Tests.Support;

/// <summary>
/// The versions this plugin targets, in one place. They appear in the build manifest, the
/// published repository, the release workflow and the README, and a test that hard-codes its own
/// copy is a test that can disagree with the others.
/// </summary>
internal static class TargetVersions
{
    /// <summary>The Jellyfin server ABI the package declares.</summary>
    public const string JellyfinAbi = "12.0.0.0";

    /// <summary>The target framework the package is built for.</summary>
    public const string Framework = "net10.0";

    /// <summary>The earliest Plugin Pages with the registration interface the plugin uses.</summary>
    public const string MinimumPluginPages = "3.0.0.0";
}
