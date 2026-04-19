using System.Collections.Generic;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.ConcertRadar.Configuration;

/// <summary>
/// Server-global plugin configuration. Persisted as XML by Jellyfin's built-in serializer.
/// All collection properties use <see cref="List{T}"/> — <c>HashSet&lt;T&gt;</c> and
/// <c>Dictionary&lt;string,T&gt;</c> do not survive a round-trip through
/// <see cref="System.Xml.Serialization.XmlSerializer"/>.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    // ── Credentials ──────────────────────────────────────────────────────────

    /// <summary>Gets or sets the Ticketmaster Discovery API key.</summary>
    public string TicketmasterApiKey { get; set; } = string.Empty;

    /// <summary>Gets or sets the Bandsintown app_id.</summary>
    public string BandsintownAppId { get; set; } = string.Empty;

    /// <summary>Gets or sets the EdmTrain API key.</summary>
    public string EdmTrainApiKey { get; set; } = string.Empty;

    // ── Source toggles ────────────────────────────────────────────────────────

    /// <summary>
    /// Gets or sets the list of enabled source identifiers.
    /// Empty by default; <see cref="PluginConfigurationDefaults.SeedIfEmpty"/>
    /// populates the spec defaults on first startup. Seeding at construction
    /// would cause <see cref="System.Xml.Serialization.XmlSerializer"/> to
    /// duplicate entries on every round-trip (it calls <c>Add</c> on the
    /// existing list instead of replacing it).
    /// </summary>
    public List<string> EnabledSources { get; set; } = new();

    /// <summary>Gets or sets a value indicating whether the user has accepted the Resident Advisor scrape ToS.</summary>
    public bool AcceptRaScrapeTos { get; set; } = false;

    /// <summary>Gets or sets a value indicating whether the user has accepted the Dice.fm scrape ToS.</summary>
    public bool AcceptDiceScrapeTos { get; set; } = false;

    /// <summary>
    /// Gets or sets a value indicating whether the user has accepted the Songkick scrape ToS.
    /// Songkick's Terms of Service prohibit scraping; operator must explicitly opt in.
    /// </summary>
    public bool AcceptSongkickScrapeTos { get; set; } = false;

    // ── Filters ───────────────────────────────────────────────────────────────

    /// <summary>Gets or sets the list of geographic locations to filter by.</summary>
    public List<LocationFilter> Locations { get; set; } = new();

    /// <summary>
    /// Gets or sets the ISO-3166 alpha-2 country codes to include (empty = all countries).
    /// Using <see cref="List{T}"/> for XML-serializer compatibility.
    /// </summary>
    public List<string> CountryAllowlist { get; set; } = new();

    /// <summary>
    /// Gets or sets the genre names to include (empty = all genres).
    /// Using <see cref="List{T}"/> for XML-serializer compatibility.
    /// </summary>
    public List<string> GenreAllowlist { get; set; } = new();

    /// <summary>Gets or sets the minimum number of days from now to include events (default 0).</summary>
    public int MinDaysAhead { get; set; } = 0;

    /// <summary>Gets or sets the maximum number of days from now to include events (default 365).</summary>
    public int MaxDaysAhead { get; set; } = 365;

    /// <summary>Gets or sets a value indicating whether festival events should be excluded.</summary>
    public bool SkipFestivals { get; set; } = false;

    /// <summary>Gets or sets a value indicating whether sold-out events should be excluded.</summary>
    public bool SkipSoldOut { get; set; } = false;

    // ── Scheduler ─────────────────────────────────────────────────────────────

    /// <summary>Gets or sets the maximum number of artists to process per scheduled run (default 50).</summary>
    public int MaxArtistsPerRun { get; set; } = 50;

    /// <summary>Gets or sets the maximum API calls per source per day (default 500).</summary>
    public int PerSourceDailyBudget { get; set; } = 500;

    /// <summary>Gets or sets the number of days after which unseen concert records are purged (default 14).</summary>
    public int StaleRecordDays { get; set; } = 14;

    /// <summary>Gets or sets the consecutive failure count that opens the circuit breaker (default 5).</summary>
    public int CircuitBreakerThreshold { get; set; } = 5;

    /// <summary>Gets or sets the number of hours to keep the circuit open after it trips (default 24).</summary>
    public int CircuitBreakerCooldownHours { get; set; } = 24;

    // ── Rate limits ───────────────────────────────────────────────────────────

    /// <summary>
    /// Gets or sets per-source rate-limit overrides.
    /// Stored as a list of key/value entries because <c>Dictionary&lt;string, RateLimitConfig&gt;</c>
    /// cannot be serialized by <see cref="System.Xml.Serialization.XmlSerializer"/>.
    /// Empty by default; <see cref="PluginConfigurationDefaults.SeedIfEmpty"/>
    /// populates the spec-default entries on first startup. Seeding at
    /// construction would cause the serializer to double the list on every
    /// round-trip (Add, not replace).
    /// </summary>
    public List<SourceRateLimitEntry> RateLimits { get; set; } = new();
}
