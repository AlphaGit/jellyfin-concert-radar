namespace Jellyfin.Plugin.ConcertRadar.Tests.Support;

/// <summary>
/// Locates files that live at the repository root rather than in the test output. The packaging
/// and documentation behaviours are about those files, so the tests must read the real ones.
/// </summary>
internal static class RepositoryFiles
{
    /// <summary>Gets the repository root, found by walking up from the test binaries.</summary>
    public static DirectoryInfo Root { get; } = FindRoot();

    /// <summary>Reads a repository-root-relative file.</summary>
    /// <param name="relativePath">Path relative to the repository root.</param>
    public static string ReadAllText(string relativePath)
        => File.ReadAllText(Path.Combine(Root.FullName, relativePath));

    /// <summary>
    /// Reads a workflow with its comment lines removed. What a workflow does must be judged on the
    /// steps that run, not on prose that happens to name a command.
    /// </summary>
    /// <param name="relativePath">Path relative to the repository root.</param>
    public static string WorkflowSteps(string relativePath)
        => string.Join(
            '\n',
            ReadAllText(relativePath).Split('\n').Where(line => !line.TrimStart().StartsWith('#')));

    /// <summary>Reports whether a repository-root-relative file exists.</summary>
    /// <param name="relativePath">Path relative to the repository root.</param>
    public static bool Exists(string relativePath)
        => File.Exists(Path.Combine(Root.FullName, relativePath));

    private static DirectoryInfo FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "build.yaml")))
        {
            directory = directory.Parent;
        }

        return directory
               ?? throw new InvalidOperationException("No build.yaml above the test binaries; cannot locate the repository root.");
    }

    /// <summary>
    /// Reads one scalar from the flat part of a YAML mapping. No YAML library is referenced by
    /// this project, and <c>build.yaml</c> is a flat mapping plus one list, so this is enough.
    /// </summary>
    /// <param name="yaml">The document text.</param>
    /// <param name="key">The key to read.</param>
    public static string? Scalar(string yaml, string key)
    {
        foreach (var line in yaml.Split('\n'))
        {
            if (!line.StartsWith(key + ":", StringComparison.Ordinal))
            {
                continue;
            }

            return line[(key.Length + 1)..].Trim().Trim('"');
        }

        return null;
    }

    /// <summary>Reads a block-sequence of quoted scalars under <paramref name="key"/>.</summary>
    /// <param name="yaml">The document text.</param>
    /// <param name="key">The key whose list to read.</param>
    public static IReadOnlyList<string> Sequence(string yaml, string key)
    {
        var items = new List<string>();
        var inside = false;

        foreach (var line in yaml.Split('\n'))
        {
            if (line.StartsWith(key + ":", StringComparison.Ordinal))
            {
                inside = true;
                continue;
            }

            if (!inside)
            {
                continue;
            }

            var trimmed = line.Trim();
            if (trimmed.StartsWith("- ", StringComparison.Ordinal))
            {
                items.Add(trimmed[2..].Trim().Trim('"'));
            }
            else if (trimmed.Length > 0)
            {
                break;
            }
        }

        return items;
    }
}
