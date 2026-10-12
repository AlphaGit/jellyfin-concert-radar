using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.ConcertRadar.Integration;
using Jellyfin.Plugin.ConcertRadar.Tests.Support;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using FakePluginPages = Jellyfin.Plugin.PluginPages.PluginInterface;

namespace Jellyfin.Plugin.ConcertRadar.Tests.Integration;

/// <summary>
/// The Concerts entry reaches the web client's menu through Plugin Pages, which may not be
/// installed. Ported from the sibling New Releases plugin, plus the one-off removal of the entry
/// 0.1.x wrote into Plugin Pages' legacy <c>config.json</c>.
/// </summary>
[Collection(PluginPagesCollection.Name)]
public sealed class PluginPagesRegistrationTests : IDisposable
{
    private readonly string _configurationsPath =
        Path.Combine(Path.GetTempPath(), "cr-pluginpages-" + Guid.NewGuid().ToString("N"));

    public PluginPagesRegistrationTests() => FakePluginPages.Reset();

    public void Dispose()
    {
        if (Directory.Exists(_configurationsPath))
        {
            Directory.Delete(_configurationsPath, recursive: true);
        }
    }

    private string LegacyConfigFile =>
        Path.Combine(_configurationsPath, "Jellyfin.Plugin.PluginPages", "config.json");

    /// <summary>The assemblies a server with Plugin Pages installed would offer the gateway.</summary>
    private static IEnumerable<Assembly> WithPluginPages() => [typeof(FakePluginPages).Assembly];

    private IApplicationPaths Paths()
    {
        var paths = Substitute.For<IApplicationPaths>();
        paths.PluginConfigurationsPath.Returns(_configurationsPath);
        return paths;
    }

    private PluginPagesRegistrationService ServiceOver(
        IEnumerable<Assembly> assemblies,
        ILogger<PluginPagesRegistrationService>? logger = null)
        => new(
            new PluginPagesGateway(() => assemblies),
            Paths(),
            logger ?? NullLogger<PluginPagesRegistrationService>.Instance);

    private void WriteLegacyConfig(string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LegacyConfigFile)!);
        File.WriteAllText(LegacyConfigFile, text);
    }

    /// <summary>
    /// The gateway the registrator builds must see real loaded assemblies. Every other test
    /// injects its own source, so they would stay green if the registrator handed it nothing.
    /// </summary>
    [Fact]
    public async Task TheGatewayTheRegistratorBuilds_CanReachATypeInALoadedAssembly()
    {
        await using var provider = HostContainer.AsTheHostWouldBuildIt();
        var gateway = provider.GetRequiredService<PluginPagesGateway>();

        Assert.True(
            gateway.TryRegisterPage("""{"Id":"probe","Url":"/u","DisplayText":"d","Icon":"i"}"""),
            "the registrator gave the gateway an assembly source that cannot see loaded assemblies");
        Assert.Equal("probe", ProbeIdOf(Assert.Single(FakePluginPages.Registered)));
    }

    [Fact]
    public async Task TheRegistrator_AddsTheRegistrationAsAHostedService()
    {
        await using var provider = HostContainer.AsTheHostWouldBuildIt();

        Assert.Contains(
            provider.GetServices<Microsoft.Extensions.Hosting.IHostedService>(),
            service => service is PluginPagesRegistrationService);
    }

    private static string? ProbeIdOf(Jellyfin.Plugin.PluginPages.FakePayload payload)
        => JsonSerializer.Deserialize<Dictionary<string, string>>(payload.Json)?["Id"];

    [Fact]
    public async Task StartAsync_WithTheIntegrationPresent_CallsRegisterPageExactlyOnce()
    {
        await ServiceOver(WithPluginPages()).StartAsync(CancellationToken.None);

        Assert.Single(FakePluginPages.Registered);
    }

    /// <summary>
    /// The four fields Plugin Pages renders. No <c>Version</c>: that was 0.1.x's own re-seed
    /// marker for the legacy file, and Plugin Pages' model has no such field.
    /// </summary>
    [Fact]
    public async Task StartAsync_SendsThePageEntry_WithExactlyFourFields()
    {
        await ServiceOver(WithPluginPages()).StartAsync(CancellationToken.None);

        var payload = JsonSerializer.Deserialize<Dictionary<string, string>>(
            Assert.Single(FakePluginPages.Registered).Json);

        Assert.NotNull(payload);
        Assert.Equal("Jellyfin.Plugin.ConcertRadar", payload["Id"]);
        Assert.Equal("/Plugins/ConcertRadar/UserView", payload["Url"]);
        Assert.Equal("Concerts", payload["DisplayText"]);
        Assert.Equal("event", payload["Icon"]);
        Assert.Equal(["DisplayText", "Icon", "Id", "Url"], payload.Keys.OrderBy(key => key, StringComparer.Ordinal));
    }

    [Fact]
    public async Task StopAsync_WithTheIntegrationPresent_CallsRemovePageOnceWithThePluginId()
    {
        var service = ServiceOver(WithPluginPages());
        await service.StartAsync(CancellationToken.None);

        await service.StopAsync(CancellationToken.None);

        Assert.Equal("Jellyfin.Plugin.ConcertRadar", Assert.Single(FakePluginPages.Removed));
    }

    [Fact]
    public async Task WithNoIntegrationAssembly_StartingAndStoppingBothSucceed()
    {
        var service = ServiceOver([]);

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        Assert.Empty(FakePluginPages.Registered);
        Assert.Empty(FakePluginPages.Removed);
    }

    [Fact]
    public async Task WithNoIntegrationAssembly_LogsExactlyOnce_BelowErrorLevel()
    {
        var logger = new RecordingLogger<PluginPagesRegistrationService>();

        await ServiceOver([], logger).StartAsync(CancellationToken.None);

        var entry = Assert.Single(logger.Entries);
        Assert.True(entry.Level < LogLevel.Error, $"logged at {entry.Level}, which reads as a fault");
    }

    [Fact]
    public async Task WhenRegisterPageThrows_StartingDoesNotThrow_AndLogsOnce()
    {
        FakePluginPages.RegisterThrows = new InvalidOperationException("Plugin Pages is not ready");
        var logger = new RecordingLogger<PluginPagesRegistrationService>();

        await ServiceOver(WithPluginPages(), logger).StartAsync(CancellationToken.None);

        var entry = Assert.Single(logger.Entries);
        Assert.True(entry.Level < LogLevel.Error, $"logged at {entry.Level}; a throwing optional integration is not a fault");
    }

    [Fact]
    public async Task StartingTwiceWithNoIntegration_LogsAtMostOnce()
    {
        var logger = new RecordingLogger<PluginPagesRegistrationService>();
        var service = ServiceOver([], logger);

        await service.StartAsync(CancellationToken.None);
        await service.StartAsync(CancellationToken.None);

        Assert.Single(logger.Entries);
    }

    // ── Legacy config.json cleanup ──────────────────────────────────────────

    [Fact]
    public async Task StartAsync_RemovesOurLegacyEntry_AndKeepsEveryOtherEntry()
    {
        WriteLegacyConfig("""
            {"pages":[
              {"Id":"Other.Plugin","Url":"/x","DisplayText":"X","Icon":"star"},
              {"Id":"Jellyfin.Plugin.ConcertRadar","Url":"/Plugins/ConcertRadar/UserView","DisplayText":"Concerts","Icon":"event","Version":1}
            ],"other":"kept"}
            """);

        await ServiceOver(WithPluginPages()).StartAsync(CancellationToken.None);

        var root = JsonNode.Parse(File.ReadAllText(LegacyConfigFile))!.AsObject();
        var page = Assert.Single(root["pages"]!.AsArray());
        Assert.Equal("Other.Plugin", (string?)page!["Id"]);
        Assert.Equal("kept", (string?)root["other"]);
    }

    [Fact]
    public async Task StartAsync_WithoutOurLegacyEntry_LeavesTheFileUntouched()
    {
        const string Original = """{"pages":[{"Id":"Other.Plugin","Url":"/x"}]}""";
        WriteLegacyConfig(Original);

        await ServiceOver(WithPluginPages()).StartAsync(CancellationToken.None);

        Assert.Equal(Original, File.ReadAllText(LegacyConfigFile));
    }

    [Fact]
    public async Task StartAsync_WithAMalformedLegacyFile_StillRegisters()
    {
        WriteLegacyConfig("{ not json");

        await ServiceOver(WithPluginPages()).StartAsync(CancellationToken.None);

        Assert.Single(FakePluginPages.Registered);
        Assert.Equal("{ not json", File.ReadAllText(LegacyConfigFile));
    }

    [Fact]
    public async Task StartAsync_WithNoLegacyFile_CreatesNothing()
    {
        await ServiceOver(WithPluginPages()).StartAsync(CancellationToken.None);

        Assert.Single(FakePluginPages.Registered);
        Assert.False(File.Exists(LegacyConfigFile));
    }
}
