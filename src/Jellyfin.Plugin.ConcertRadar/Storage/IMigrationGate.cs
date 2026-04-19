using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.ConcertRadar.Storage;

/// <summary>
/// Guards repository access until schema migration has completed.
/// Implementations signal readiness once; callers await <see cref="WaitAsync"/>
/// before opening a connection for the first time.
/// </summary>
public interface IMigrationGate
{
    /// <summary>
    /// Returns a <see cref="Task"/> that completes when schema migration is done.
    /// Always returns immediately after the first call to <see cref="SignalReady"/>.
    /// </summary>
    Task WaitAsync(CancellationToken ct);

    /// <summary>
    /// Signals that migration is complete. Safe to call multiple times (idempotent).
    /// </summary>
    void SignalReady();
}
