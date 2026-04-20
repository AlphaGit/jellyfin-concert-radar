using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.ConcertRadar.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.ConcertRadar;

/// <summary>
/// Jellyfin Concert Radar plugin entry point.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    // Bump when the Plugin Pages entry schema changes; forces re-seed of the
    // Plugin Pages config.json so stale entries get replaced.
    private const int PluginPagesEntryVersion = 1;

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;

        TryRegisterPluginPagesEntry(applicationPaths);
    }

    /// <inheritdoc />
    public override string Name => "Concert Radar";

    /// <inheritdoc />
    public override string Description =>
        "Aggregates upcoming concerts for the artists in your Jellyfin music library from multiple sources "
        + "(Ticketmaster, Bandsintown, EdmTrain, Songkick, Dice.fm, Resident Advisor), normalizes them, and "
        + "surfaces them in admin and user-accessible views.";

    /// <inheritdoc />
    public override Guid Id => new Guid("65bb36d5-70d5-4121-8771-3dd404d20287");

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages() => new[]
    {
        new PluginPageInfo
        {
            Name = "concertradar",
            EmbeddedResourcePath = GetType().Namespace + ".Web.admin.html",
        },
        new PluginPageInfo
        {
            Name                 = "concertradar-view",
            EmbeddedResourcePath = GetType().Namespace + ".Web.view.html",
        },
    };

    /// <summary>
    /// Writes a Concert Radar entry into the Plugin Pages (by IAmParadox27)
    /// config file so the user-facing view appears in the web client's
    /// hamburger menu. No-op if Plugin Pages is not installed; the directory
    /// only exists once that plugin has run at least once.
    /// Pattern borrowed from jellyfin-plugin-home-sections, the reference
    /// consumer of Plugin Pages.
    /// </summary>
    private static void TryRegisterPluginPagesEntry(IApplicationPaths applicationPaths)
    {
        try
        {
            var configDir = Path.Combine(applicationPaths.PluginConfigurationsPath, "Jellyfin.Plugin.PluginPages");
            var configFile = Path.Combine(configDir, "config.json");

            JsonObject root;
            if (File.Exists(configFile))
            {
                var text = File.ReadAllText(configFile);
                root = string.IsNullOrWhiteSpace(text)
                    ? new JsonObject()
                    : JsonNode.Parse(text) as JsonObject ?? new JsonObject();
            }
            else
            {
                // Create the directory only if Plugin Pages is actually present.
                // Presence check: look for the plugin's DLL in plugins/.
                if (!IsPluginPagesInstalled(applicationPaths))
                {
                    return;
                }

                Directory.CreateDirectory(configDir);
                root = new JsonObject();
            }

            if (root["pages"] is not JsonArray pages)
            {
                pages = new JsonArray();
                root["pages"] = pages;
            }

            // Version-gated replace: drop any stale Concert Radar entry.
            JsonObject? existing = null;
            foreach (var node in pages)
            {
                if (node is JsonObject obj && (string?)obj["Id"] == "Jellyfin.Plugin.ConcertRadar")
                {
                    existing = obj;
                    break;
                }
            }
            if (existing is not null)
            {
                var storedVersion = (int?)existing["Version"] ?? 0;
                if (storedVersion >= PluginPagesEntryVersion)
                {
                    return;
                }
                pages.Remove(existing);
            }

            pages.Add(new JsonObject
            {
                ["Id"]          = "Jellyfin.Plugin.ConcertRadar",
                ["Url"]         = "/Plugins/ConcertRadar/UserView",
                ["DisplayText"] = "Concerts",
                ["Icon"]        = "event",
                ["Version"]     = PluginPagesEntryVersion,
            });

            File.WriteAllText(configFile, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Intentionally swallow: Plugin Pages is an optional integration.
            // Core plugin functionality (admin config page, scheduled task, API)
            // must start regardless of whether the hamburger menu entry is wired.
        }
    }

    private static bool IsPluginPagesInstalled(IApplicationPaths applicationPaths)
    {
        try
        {
            var pluginsDir = applicationPaths.PluginsPath;
            if (!Directory.Exists(pluginsDir))
            {
                return false;
            }
            foreach (var dir in Directory.EnumerateDirectories(pluginsDir))
            {
                var name = Path.GetFileName(dir);
                if (name is null) continue;
                if (name.StartsWith("PluginPages", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("Jellyfin.Plugin.PluginPages", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }
        catch
        {
            return false;
        }
    }
}
