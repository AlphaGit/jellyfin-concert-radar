using System.IO;
using System.Linq;
using System.Xml.Serialization;
using FluentAssertions;
using Jellyfin.Plugin.ConcertRadar.Configuration;
using Xunit;

namespace Jellyfin.Plugin.ConcertRadar.Tests.Configuration;

/// <summary>
/// Unit tests for <see cref="PluginConfiguration"/> defaults and XML serialization.
/// </summary>
public class PluginConfigurationTests
{
    // ── Defaults ──────────────────────────────────────────────────────────────

    [Fact]
    public void PluginConfigurationDefaults_EmptyCollectionsOnConstruction()
    {
        var cfg = new PluginConfiguration();

        cfg.TicketmasterApiKey.Should().BeEmpty();
        cfg.BandsintownAppId.Should().BeEmpty();
        cfg.EdmTrainApiKey.Should().BeEmpty();

        // Collections empty by construction — XmlSerializer would duplicate entries on
        // every round-trip if the ctor seeded them. Seeding is done by
        // PluginConfigurationDefaults.SeedIfEmpty at startup.
        cfg.EnabledSources.Should().BeEmpty();
        cfg.RateLimits.Should().BeEmpty();
        cfg.Locations.Should().BeEmpty();
        cfg.CountryAllowlist.Should().BeEmpty();
        cfg.GenreAllowlist.Should().BeEmpty();

        // Scalar defaults per SPEC §5
        cfg.AcceptRaScrapeTos.Should().BeFalse();
        cfg.AcceptDiceScrapeTos.Should().BeFalse();
        cfg.MinDaysAhead.Should().Be(0);
        cfg.MaxDaysAhead.Should().Be(365);
        cfg.SkipFestivals.Should().BeFalse();
        cfg.SkipSoldOut.Should().BeFalse();
        cfg.MaxArtistsPerRun.Should().Be(50);
        cfg.PerSourceDailyBudget.Should().Be(500);
        cfg.StaleRecordDays.Should().Be(14);
        cfg.CircuitBreakerThreshold.Should().Be(5);
        cfg.CircuitBreakerCooldownHours.Should().Be(24);
    }

    [Fact]
    public void PluginConfigurationDefaults_SeedIfEmpty_MatchesSpec()
    {
        var cfg = new PluginConfiguration();

        var changed = PluginConfigurationDefaults.SeedIfEmpty(cfg);
        changed.Should().BeTrue();

        cfg.EnabledSources.Should().BeEquivalentTo(
            new[] { "ticketmaster", "bandsintown", "songkick" },
            because: "SPEC §5 lists these three as defaults");

        cfg.RateLimits.Should().HaveCount(6,
            because: "SPEC §5 rate-limit table has exactly six sources");

        AssertRateLimit(cfg, "ticketmaster", requestsPerSecond: 5,    requestsPerDay: 5000);
        AssertRateLimit(cfg, "bandsintown",  requestsPerSecond: 1,    requestsPerDay: null);
        AssertRateLimit(cfg, "edmtrain",     requestsPerSecond: 1,    requestsPerDay: null);
        AssertRateLimit(cfg, "songkick",     requestsPerSecond: 1,    requestsPerDay: null);
        AssertRateLimit(cfg, "dice",         requestsPerSecond: 0.5,  requestsPerDay: null);
        AssertRateLimit(cfg, "ra",           requestsPerSecond: 0.5,  requestsPerDay: null);
    }

    [Fact]
    public void PluginConfigurationDefaults_SeedIfEmpty_IsIdempotent()
    {
        var cfg = new PluginConfiguration();

        PluginConfigurationDefaults.SeedIfEmpty(cfg).Should().BeTrue();
        PluginConfigurationDefaults.SeedIfEmpty(cfg).Should().BeFalse(
            because: "subsequent calls must not duplicate entries or overwrite user edits");

        cfg.EnabledSources.Should().HaveCount(3);
        cfg.RateLimits.Should().HaveCount(6);
    }

    // ── XML round-trip ────────────────────────────────────────────────────────

    [Fact]
    public void PluginConfiguration_RoundTripsAsXml()
    {
        var original = new PluginConfiguration
        {
            TicketmasterApiKey = "tm-key-abc",
            BandsintownAppId   = "bit-app-id",
            EdmTrainApiKey     = "edm-key-xyz",
            AcceptRaScrapeTos  = true,
            AcceptDiceScrapeTos = true,
            MinDaysAhead  = 3,
            MaxDaysAhead  = 180,
            SkipFestivals = true,
            SkipSoldOut   = true,
            MaxArtistsPerRun         = 25,
            PerSourceDailyBudget     = 200,
            StaleRecordDays          = 7,
            CircuitBreakerThreshold  = 3,
            CircuitBreakerCooldownHours = 12,
        };
        PluginConfigurationDefaults.SeedIfEmpty(original);
        original.EnabledSources.Add("dice");
        original.CountryAllowlist.Add("US");
        original.CountryAllowlist.Add("GB");
        original.GenreAllowlist.Add("Rock");
        original.Locations.Add(new LocationFilter { City = "London", Country = "GB", RadiusKm = 50 });

        var serializer = new XmlSerializer(typeof(PluginConfiguration));

        // Serialize
        string xml;
        using (var sw = new StringWriter())
        {
            serializer.Serialize(sw, original);
            xml = sw.ToString();
        }

        // Deserialize
        PluginConfiguration? restored;
        using (var sr = new StringReader(xml))
        {
            restored = (PluginConfiguration?)serializer.Deserialize(sr);
        }

        restored.Should().NotBeNull();
        restored!.TicketmasterApiKey.Should().Be("tm-key-abc");
        restored.BandsintownAppId.Should().Be("bit-app-id");
        restored.EdmTrainApiKey.Should().Be("edm-key-xyz");
        restored.AcceptRaScrapeTos.Should().BeTrue();
        restored.AcceptDiceScrapeTos.Should().BeTrue();
        restored.MinDaysAhead.Should().Be(3);
        restored.MaxDaysAhead.Should().Be(180);
        restored.SkipFestivals.Should().BeTrue();
        restored.SkipSoldOut.Should().BeTrue();
        restored.MaxArtistsPerRun.Should().Be(25);
        restored.PerSourceDailyBudget.Should().Be(200);
        restored.StaleRecordDays.Should().Be(7);
        restored.CircuitBreakerThreshold.Should().Be(3);
        restored.CircuitBreakerCooldownHours.Should().Be(12);

        // Collections
        restored.EnabledSources.Should().Contain("dice");
        restored.EnabledSources.Should().Contain("ticketmaster");
        restored.CountryAllowlist.Should().BeEquivalentTo(new[] { "US", "GB" });
        restored.GenreAllowlist.Should().BeEquivalentTo(new[] { "Rock" });
        restored.Locations.Should().ContainSingle(l => l.City == "London" && l.Country == "GB" && l.RadiusKm == 50);

        // Collections must survive exactly — not double or drop. Defaults are empty in the ctor so
        // XmlSerializer's "Add to existing list" behaviour cannot duplicate them.
        restored.EnabledSources.Should().BeEquivalentTo(original.EnabledSources);
        restored.RateLimits.Should().BeEquivalentTo(original.RateLimits);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void AssertRateLimit(
        PluginConfiguration cfg,
        string source,
        double requestsPerSecond,
        int? requestsPerDay)
    {
        var entry = cfg.RateLimits.SingleOrDefault(e => e.Source == source);
        entry.Should().NotBeNull(because: $"source '{source}' should have a default rate-limit entry");
        entry!.Config.RequestsPerSecond.Should().Be(requestsPerSecond,
            because: $"SPEC §5 specifies {requestsPerSecond} req/s for {source}");
        entry.Config.RequestsPerDay.Should().Be(requestsPerDay,
            because: $"SPEC §5 specifies {(requestsPerDay.HasValue ? requestsPerDay.ToString() : "unlimited")} req/day for {source}");
    }
}
