using System;
using System.IO;
using System.Text;

namespace Jellyfin.Plugin.ConcertRadar.Tests.Support;

/// <summary>
/// Loads test fixture files from <c>tests/fixtures/</c>.
/// Fixtures are copied to the test output directory via
/// <c>&lt;None Update="..." CopyToOutputDirectory="PreserveNewest" /&gt;</c>
/// in the test project file, so they are resolved relative to
/// <see cref="AppContext.BaseDirectory"/>.
/// </summary>
internal static class FixtureLoader
{
    /// <summary>
    /// Reads a fixture file as a UTF-8 string.
    /// </summary>
    /// <param name="relativePath">
    /// Path relative to the <c>fixtures/</c> root, e.g. <c>"musicbrainz/search_multi_score.json"</c>.
    /// </param>
    public static string LoadText(string relativePath)
        => File.ReadAllText(ResolvePath(relativePath), Encoding.UTF8);

    /// <summary>
    /// Reads a fixture file as raw bytes.
    /// </summary>
    /// <param name="relativePath">
    /// Path relative to the <c>fixtures/</c> root.
    /// </param>
    public static byte[] LoadBytes(string relativePath)
        => File.ReadAllBytes(ResolvePath(relativePath));

    private static string ResolvePath(string relativePath)
    {
        // When CopyToOutputDirectory is set, fixtures land alongside the test assembly.
        string fromOutput = Path.Combine(
            AppContext.BaseDirectory,
            "fixtures",
            relativePath.Replace('/', Path.DirectorySeparatorChar));

        if (File.Exists(fromOutput))
            return fromOutput;

        // Fallback: walk up from the test assembly directory to the repo root,
        // then navigate to tests/fixtures/. Handles cases where fixtures were
        // not copied (e.g. IDE "run without build").
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            string candidate = Path.Combine(
                dir.FullName,
                "tests",
                "fixtures",
                relativePath.Replace('/', Path.DirectorySeparatorChar));

            if (File.Exists(candidate))
                return candidate;

            dir = dir.Parent;
        }

        throw new FileNotFoundException(
            $"Fixture '{relativePath}' not found. " +
            $"Searched output dir '{fromOutput}' and repo-root fallback. " +
            "Ensure the file exists and CopyToOutputDirectory=PreserveNewest is set in the .csproj.",
            fromOutput);
    }
}
