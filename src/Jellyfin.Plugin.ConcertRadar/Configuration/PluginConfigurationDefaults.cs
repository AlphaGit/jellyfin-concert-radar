namespace Jellyfin.Plugin.ConcertRadar.Configuration;

/// <summary>
/// Spec-default seeding for <see cref="PluginConfiguration"/>. Kept out of the
/// configuration type itself because <see cref="System.Xml.Serialization.XmlSerializer"/>
/// calls <c>Add</c> on pre-populated collections, which would double the list on
/// every load/save cycle.
/// </summary>
public static class PluginConfigurationDefaults
{
    /// <summary>Default enabled sources per SPEC §5.</summary>
    public static readonly string[] DefaultEnabledSources =
    {
        "ticketmaster",
        "bandsintown",
        "songkick",
    };

    /// <summary>Default per-source rate limits per SPEC §5.</summary>
    public static SourceRateLimitEntry[] DefaultRateLimits() => new[]
    {
        new SourceRateLimitEntry { Source = "ticketmaster", Config = new() { RequestsPerSecond = 5,   RequestsPerDay = 5000 } },
        new SourceRateLimitEntry { Source = "bandsintown",  Config = new() { RequestsPerSecond = 1,   RequestsPerDay = null  } },
        new SourceRateLimitEntry { Source = "edmtrain",     Config = new() { RequestsPerSecond = 1,   RequestsPerDay = null  } },
        new SourceRateLimitEntry { Source = "songkick",     Config = new() { RequestsPerSecond = 1,   RequestsPerDay = null  } },
        new SourceRateLimitEntry { Source = "dice",         Config = new() { RequestsPerSecond = 0.5, RequestsPerDay = null  } },
        new SourceRateLimitEntry { Source = "ra",           Config = new() { RequestsPerSecond = 0.5, RequestsPerDay = null  } },
    };

    /// <summary>
    /// Seeds <paramref name="cfg"/> with spec defaults only for collections that
    /// are currently empty. Idempotent: re-running after user customization does
    /// not overwrite their edits.
    /// </summary>
    /// <returns>True if any defaults were applied.</returns>
    public static bool SeedIfEmpty(PluginConfiguration cfg)
    {
        var changed = false;

        if (cfg.EnabledSources.Count == 0)
        {
            cfg.EnabledSources.AddRange(DefaultEnabledSources);
            changed = true;
        }

        if (cfg.RateLimits.Count == 0)
        {
            cfg.RateLimits.AddRange(DefaultRateLimits());
            changed = true;
        }

        return changed;
    }
}
