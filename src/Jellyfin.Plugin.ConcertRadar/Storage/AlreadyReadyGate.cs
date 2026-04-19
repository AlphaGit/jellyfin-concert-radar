using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.ConcertRadar.Storage;

/// <summary>
/// An <see cref="IMigrationGate"/> that is permanently signalled.  Used by the
/// two-argument convenience constructors on each repository so that test code
/// which has already applied migrations can construct repositories without
/// instantiating a full <see cref="MigrationGate"/>.
/// </summary>
internal sealed class AlreadyReadyGate : IMigrationGate
{
    /// <summary>Gets the singleton pre-signalled gate instance.</summary>
    public static readonly AlreadyReadyGate Instance = new();

    private AlreadyReadyGate()
    {
    }

    /// <inheritdoc />
    public Task WaitAsync(CancellationToken ct) => Task.CompletedTask;

    /// <inheritdoc />
    public void SignalReady()
    {
        // No-op — already ready.
    }
}
