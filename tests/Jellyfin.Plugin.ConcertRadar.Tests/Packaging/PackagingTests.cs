using System.Text.Json.Nodes;
using Jellyfin.Plugin.ConcertRadar.Tests.Sources;
using Jellyfin.Plugin.ConcertRadar.Tests.Support;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Serialization;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.ConcertRadar.Tests.Packaging;

/// <summary>
/// <c>build.yaml</c>, <c>repo/manifest.json</c> and the release workflow are what put the plugin
/// on a server. Each fact here is one a Jellyfin 12 install depends on and no compiler checks.
/// </summary>
[Collection(PluginInstanceCollection.Name)]
public class PackagingTests
{
    private static readonly string BuildYaml = RepositoryFiles.ReadAllText("build.yaml");

    private static readonly JsonObject Catalogue =
        JsonNode.Parse(RepositoryFiles.ReadAllText("repo/manifest.json"))!.AsArray().Single()!.AsObject();

    private static Plugin NewPlugin() => new(Substitute.For<IApplicationPaths>(), Substitute.For<IXmlSerializer>());

    // ---- build.yaml ----

    /// <summary>Jellyfin offers a version only to a server at or above its targetAbi.</summary>
    [Fact]
    public void BuildManifest_DeclaresJellyfin12AsTheTargetAbi()
        => Assert.Equal(TargetVersions.JellyfinAbi, RepositoryFiles.Scalar(BuildYaml, "targetAbi"));

    /// <summary>JPRM rewrites the project's TargetFramework from build.yaml while packaging.</summary>
    [Fact]
    public void BuildManifest_FrameworkIsNet10_AndMatchesTheProjectFile()
    {
        Assert.Equal(TargetVersions.Framework, RepositoryFiles.Scalar(BuildYaml, "framework"));
        Assert.Contains(
            $"<TargetFramework>{TargetVersions.Framework}</TargetFramework>",
            RepositoryFiles.ReadAllText("src/Jellyfin.Plugin.ConcertRadar/Jellyfin.Plugin.ConcertRadar.csproj"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void BuildManifest_GuidMatchesTheOneCompiledIntoThePlugin()
        => Assert.Equal(NewPlugin().Id, Guid.Parse(RepositoryFiles.Scalar(BuildYaml, "guid")!));

    /// <summary>
    /// Jellyfin keeps the newest installed copy <em>per name</em> and retires the rest. A
    /// catalogue name that differs from the name the running plugin reports files two copies
    /// under two names, and the host loads both after an upgrade (New Releases 006).
    /// </summary>
    [Fact]
    public void TheCatalogueNames_AgreeWithTheNameThePluginReports()
    {
        var reported = NewPlugin().Name;

        Assert.Equal(reported, RepositoryFiles.Scalar(BuildYaml, "name"));
        Assert.Equal(reported, (string?)Catalogue["name"]);
    }

    [Fact]
    public void BuildManifest_DescriptionStatesTheJellyfinAndPluginPagesRequirements()
    {
        var description = RepositoryFiles.Scalar(BuildYaml, "description")!;

        Assert.Contains("Jellyfin 12", description, StringComparison.Ordinal);
        Assert.Contains("Plugin Pages (" + TargetVersions.MinimumPluginPages, description, StringComparison.Ordinal);
        Assert.Contains("File Transformation", description, StringComparison.Ordinal);
    }

    /// <summary>A catalogue install needs no manual file step, so every runtime file is listed.</summary>
    [Fact]
    public void BuildManifest_ShipsThePluginAngleSharpSqliteAndTheNativeLibrary()
    {
        Assert.Equal(
            new SortedSet<string>(
            [
                "Jellyfin.Plugin.ConcertRadar.dll",
                "AngleSharp.dll",
                "Microsoft.Data.Sqlite.dll",
                "SQLitePCLRaw.batteries_v2.dll",
                "SQLitePCLRaw.core.dll",
                "SQLitePCLRaw.provider.e_sqlite3.dll",
                "runtimes/linux-x64/native/libe_sqlite3.so",
            ], StringComparer.Ordinal),
            new SortedSet<string>(RepositoryFiles.Sequence(BuildYaml, "artifacts"), StringComparer.Ordinal));
    }

    // ---- repo/manifest.json ----

    [Fact]
    public void TheCatalogue_DescribesTheSamePluginAsBuildYaml()
    {
        Assert.Equal(RepositoryFiles.Scalar(BuildYaml, "guid"), (string?)Catalogue["guid"]);
        Assert.Equal(RepositoryFiles.Scalar(BuildYaml, "description"), (string?)Catalogue["description"]);
        Assert.Equal(RepositoryFiles.Scalar(BuildYaml, "overview"), (string?)Catalogue["overview"]);
    }

    [Fact]
    public void EveryVersionTheCatalogueLists_TargetsJellyfin12_AndIsAGitHubReleaseAsset()
    {
        foreach (var version in Catalogue["versions"]!.AsArray())
        {
            Assert.Equal(TargetVersions.JellyfinAbi, (string?)version!["targetAbi"]);
            Assert.StartsWith(
                "https://github.com/AlphaGit/jellyfin-concert-radar/releases/download/v",
                (string?)version["sourceUrl"],
                StringComparison.Ordinal);
            Assert.EndsWith("/concert-radar.zip", (string?)version["sourceUrl"], StringComparison.Ordinal);
            Assert.Matches("^[0-9a-f]{32}$", (string?)version["checksum"]);
        }
    }

    [Fact]
    public void TheRetiredRootManifest_IsGone()
        => Assert.False(RepositoryFiles.Exists("manifest.json"));

    // ---- .github/workflows ----

    [Fact]
    public void TheReleaseWorkflow_PublishesAReleaseAsset_AndCommitsTheCatalogue()
    {
        var steps = RepositoryFiles.WorkflowSteps(".github/workflows/package.yml");

        Assert.Contains("dotnet-version: '10.0.x'", steps, StringComparison.Ordinal);
        Assert.Contains("pip install jprm==1.1.0", steps, StringComparison.Ordinal);
        Assert.Contains("node .github/scripts/changelog-entry.js", steps, StringComparison.Ordinal);
        Assert.Contains("gh release create", steps, StringComparison.Ordinal);
        Assert.Contains("concert-radar.zip", steps, StringComparison.Ordinal);
        Assert.Contains("jprm repo add", steps, StringComparison.Ordinal);
        Assert.Contains("./repo/manifest.json", steps, StringComparison.Ordinal);
        Assert.Contains("git add repo", steps, StringComparison.Ordinal);
        Assert.Contains("contents: write", steps, StringComparison.Ordinal);
    }

    [Fact]
    public void TheBuildWorkflow_BuildsAndTestsOnNet10_AndRunsThePageTests()
    {
        var steps = RepositoryFiles.WorkflowSteps(".github/workflows/build.yml");

        Assert.Contains("dotnet-version: '10.0.x'", steps, StringComparison.Ordinal);
        Assert.Contains("dotnet test", steps, StringComparison.Ordinal);
        Assert.Contains("node --test", steps, StringComparison.Ordinal);
    }
}
