namespace Jellyfin.Plugin.ConcertRadar.Model;

/// <summary>
/// Operational status of a concert source adapter.
/// </summary>
public enum SourceStatus
{
    /// <summary>Source is reachable and returning results normally.</summary>
    Ok,

    /// <summary>Source is reachable but returning partial or degraded results.</summary>
    Degraded,

    /// <summary>Source has tripped the circuit breaker due to repeated failures.</summary>
    Failing,

    /// <summary>Source has been manually disabled by an administrator.</summary>
    Disabled,

    /// <summary>Source has not been configured (missing API key etc.).</summary>
    Unconfigured,
}
