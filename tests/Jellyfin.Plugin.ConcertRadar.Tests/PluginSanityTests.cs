using FluentAssertions;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Serialization;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.ConcertRadar.Tests;

/// <summary>
/// Sanity tests that verify fundamental plugin invariants without requiring a running Jellyfin host.
/// </summary>
public class PluginSanityTests
{
    /// <summary>
    /// Verifies that the plugin GUID has not changed from the value committed to source.
    /// Changing it would break existing installations (Jellyfin uses the GUID as the plugin identity key).
    /// </summary>
    [Fact]
    public void Plugin_Guid_IsStable()
    {
        var applicationPaths = Substitute.For<IApplicationPaths>();
        var xmlSerializer = Substitute.For<IXmlSerializer>();

        var plugin = new Plugin(applicationPaths, xmlSerializer);

        plugin.Id.Should().Be(new Guid("65bb36d5-70d5-4121-8771-3dd404d20287"));
    }
}
