using Xunit;

namespace Jellyfin.Plugin.ConcertRadar.Tests.Support;

/// <summary>
/// Tests that touch the static <c>Jellyfin.Plugin.PluginPages.PluginInterface</c> stand-in, which
/// the production gateway can reach because it scans every loaded assembly. They must not run
/// beside each other.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PluginPagesCollection
{
    public const string Name = "process-global Plugin Pages state";
}
