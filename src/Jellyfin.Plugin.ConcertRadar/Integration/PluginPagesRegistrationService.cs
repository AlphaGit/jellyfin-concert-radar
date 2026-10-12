using System.Text.Json;
using System.Text.Json.Nodes;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ConcertRadar.Integration;

/// <summary>
/// Registers the Concerts page with Plugin Pages at host start and withdraws it at stop.
/// </summary>
public sealed class PluginPagesRegistrationService : IHostedService
{
    /// <summary>The entry's identity. `RemovePage` takes this string.</summary>
    public const string PageEntryId = "Jellyfin.Plugin.ConcertRadar";

    /// <summary>The page entry Plugin Pages deserialises into its own model.</summary>
    public const string PageEntryJson = """
        {
          "Id": "Jellyfin.Plugin.ConcertRadar",
          "Url": "/Plugins/ConcertRadar/UserView",
          "DisplayText": "Concerts",
          "Icon": "event"
        }
        """;

    private readonly PluginPagesGateway _gateway;
    private readonly IApplicationPaths _applicationPaths;
    private readonly ILogger<PluginPagesRegistrationService> _logger;

    private bool _reportedUnavailable;

    public PluginPagesRegistrationService(
        PluginPagesGateway gateway,
        IApplicationPaths applicationPaths,
        ILogger<PluginPagesRegistrationService> logger)
    {
        _gateway = gateway;
        _applicationPaths = applicationPaths;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        RemoveLegacyEntry();

        if (!_gateway.TryRegisterPage(PageEntryJson))
        {
            ReportUnavailableOnce();
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _gateway.TryRemovePage(PageEntryId);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Removes the entry 0.1.x wrote into Plugin Pages' legacy <c>config.json</c>. Plugin Pages
    /// keeps serving that entry after this plugin is uninstalled, so it must go once the page is
    /// registered through the API instead. Touches only our own entry; never throws.
    /// </summary>
    // ponytail: one-release migration for installs upgraded from 0.1.x; delete after 0.2.x.
    internal void RemoveLegacyEntry()
    {
        var configFile = Path.Combine(
            _applicationPaths.PluginConfigurationsPath, "Jellyfin.Plugin.PluginPages", "config.json");

        try
        {
            if (!File.Exists(configFile)
                || JsonNode.Parse(File.ReadAllText(configFile)) is not JsonObject root
                || root["pages"] is not JsonArray pages)
            {
                return;
            }

            var ours = pages
                .Where(node => node is JsonObject entry && (string?)entry["Id"] == PageEntryId)
                .ToList();
            if (ours.Count == 0)
            {
                return;
            }

            foreach (var node in ours)
            {
                pages.Remove(node);
            }

            File.WriteAllText(configFile, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            _logger.LogInformation("Removed the legacy Concerts entry from {ConfigFile}.", configFile);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not remove the legacy Concerts entry from {ConfigFile}.", configFile);
        }
    }

    /// <summary>
    /// Plugin Pages is optional, so its absence is stated once per run and never as an error.
    /// </summary>
    private void ReportUnavailableOnce()
    {
        if (_reportedUnavailable)
        {
            return;
        }

        _reportedUnavailable = true;
        _logger.LogInformation(
            "Plugin Pages is not available, so the Concerts page is not in the menu. Everything else works.");
    }
}
