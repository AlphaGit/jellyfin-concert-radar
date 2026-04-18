using Xunit;

namespace Jellyfin.Plugin.ConcertRadar.Tests.Sources;

/// <summary>
/// xUnit collection that serializes all adapter tests which mutate the static
/// <see cref="Plugin.Instance"/> singleton. Tests in this collection run sequentially
/// (not in parallel) to avoid race conditions on the static field.
/// </summary>
[CollectionDefinition(Name)]
public sealed class PluginInstanceCollection : ICollectionFixture<PluginInstanceCollection>
{
    /// <summary>Collection name constant.</summary>
    public const string Name = "PluginInstance";
}
