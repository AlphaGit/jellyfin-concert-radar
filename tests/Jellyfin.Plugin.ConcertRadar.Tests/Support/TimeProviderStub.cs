using System;

namespace Jellyfin.Plugin.ConcertRadar.Tests.Support;

/// <summary>
/// Controllable <see cref="TimeProvider"/> for deterministic time-sensitive tests.
/// </summary>
internal sealed class TimeProviderStub : TimeProvider
{
    private DateTimeOffset _utcNow;

    public TimeProviderStub(DateTimeOffset utcNow)
    {
        _utcNow = utcNow;
    }

    /// <summary>Advances or rewinds the stub clock to the given instant.</summary>
    public void Set(DateTimeOffset utcNow) => _utcNow = utcNow;

    /// <summary>Advances the clock by the given duration.</summary>
    public void Advance(TimeSpan delta) => _utcNow = _utcNow.Add(delta);

    public override DateTimeOffset GetUtcNow() => _utcNow;
}
